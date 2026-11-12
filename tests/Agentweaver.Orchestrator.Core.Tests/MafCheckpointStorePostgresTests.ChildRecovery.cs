using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class MafCheckpointStorePostgresTests
{
    private static readonly JsonSerializerOptions ChildRecoveryJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [Theory]
    [InlineData("absent", "pendingUnsent")]
    [InlineData("prepared", "pendingUnsent")]
    [InlineData("sending", "unresolved")]
    public async Task ChildRecoveryCommitsFrozenManifestAndHoldWithFenceAndExactReplay(
        string nativeState, string expectedDisposition)
    {
        var fixture = await PrepareChildRecoveryAsync(nativeState: nativeState);
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        Assert.Equal(_binding.ExecutionFence + 1, recovered.ExecutionFence);
        Assert.Equal("pending", recovered.CheckpointRecovery!.Phase);
        Assert.Equal("childJoin", recovered.CheckpointRecovery.BoundaryKind);
        Assert.Equal(true, recovered.CheckpointRecovery.DispatchHeld);
        var restarted = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, fixture.Decisions);
        var replay = await restarted.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        Assert.True(replay.IsDuplicate);
        Assert.Equal(recovered.OperationId, replay.OperationId);
        Assert.Equal(recovered.ExecutionFence, replay.ExecutionFence);
        var conflictingReplay = await Assert.ThrowsAsync<CoordinationException>(() =>
            restarted.RecoverRunExecutionAsync(
                _actor, _binding.Identity.ProjectId, _binding.Identity.RunId,
                fixture.Request with { Reference = "different-reference" },
                CancellationToken.None, _selection, _executionStore));
        Assert.Equal("execution_operation_idempotency_conflict", conflictingReplay.Code);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand($"""
            SELECT count(*), min(progress ->> 'dispatchHeld'),
                   min(progress ->> 'childManifestSha256'),
                   min(result -> 'checkpointRecovery' ->> 'boundaryKind'),
                   min(progress ->> 'childManifest')
            FROM "{_schema}".coordination_execution_operations
            WHERE project_id = @project AND run_id = @run AND operation_kind = 'recovery'
            """, connection);
        verify.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
        verify.Parameters.AddWithValue("run", _binding.Identity.RunId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("true", reader.GetString(1));
        Assert.Equal(recovered.CheckpointRecovery.ChildManifestSha256, reader.GetString(2));
        Assert.Equal("childJoin", reader.GetString(3));
        var manifest = ChildCheckpointRecoveryManifestCodec.Decode(reader.GetString(4), reader.GetString(2));
        var member = Assert.Single(manifest.Members);
        Assert.Equal(fixture.Child.SessionId, member.Session.SessionId);
        Assert.Equal(nativeState == "absent", member.NativeOriginProvenAbsent);
        Assert.Equal(nativeState == "absent" ? null : nativeState, member.NativeOrigin?.State);
        Assert.Equal(expectedDisposition, member.Disposition);
        Assert.Equal(nativeState == "sending" ? "native_send_or_accounting_unresolved" : null,
            member.Reason);
        Assert.Equal(fixture.Source.Info.CheckpointId, manifest.SourceCheckpointId);
        Assert.Equal(recovered.OperationId, manifest.OperationId);
        Assert.Equal(recovered.ExecutionFence, manifest.HoldFence);
    }

    [Fact]
    public async Task ChildRecoveryHoldDeniesConcurrentCheckpointStartupAndDifferentRecovery()
    {
        var fixture = await PrepareChildRecoveryAsync();
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var continuationAttempt = await Assert.ThrowsAsync<CoordinationException>(() =>
            fixture.Owner.UpdateCheckpointRecoveryProgressAsync(
                _actor, _binding.Identity, recovered.OperationId,
                recovered.CheckpointRecovery! with
                {
                    Phase = "ready",
                    CurrentStep = "ready",
                    DispatchHeld = false
                },
                CancellationToken.None));
        Assert.Equal("checkpoint_recovery_members_not_ready", continuationAttempt.Code);
        var current = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null),
            _binding with { ExecutionFence = recovered.ExecutionFence });
        var checkpoint = await current.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(checkpoint);
        var candidate = checkpoint.State with { Revision = checkpoint.State.Revision + 1 };
        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);
        var restartOwner = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, fixture.Decisions);
        var otherRequest = fixture.Request with
        {
            ExecutionFence = recovered.ExecutionFence,
            ExpectedRunStateVersion = recovered.RunStateVersion,
            IdempotencyKey = "another-child-recovery"
        };

        var checkpointAttempt = Assert.ThrowsAsync<CoordinationException>(() =>
            current.AppendAsync("held-checkpoint", checkpoint.Info, candidate, CancellationToken.None));
        var startAttempt = Assert.ThrowsAsync<CoordinationException>(() =>
            starts.UpdateAsync(fixture.ChildIntent, fixture.ChildIntent.RowVersion,
                "pending", "accepted", null, null, null,
                JsonSerializer.Deserialize<JsonElement>(fixture.ChildIntent.ReceiptLinks), CancellationToken.None));
        var recoveryAttempt = Assert.ThrowsAsync<CoordinationException>(() =>
            restartOwner.RecoverRunExecutionAsync(
                _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, otherRequest,
                CancellationToken.None, _selection, current));
        var suspendAttempt = Assert.ThrowsAsync<CoordinationException>(() =>
            restartOwner.BeginSessionSuspendAsync(
                _actor, _selection, fixture.Child, "held-suspend", CancellationToken.None));
        var rootRegistrationAttempt = Assert.ThrowsAsync<CoordinationException>(() =>
            restartOwner.ReadRuntimeOwnerStateForRegistrationAsync(
                _actor, _binding.Identity, "tenant-1",
                CoordinationOwnerStore.HashSelection(_selection.Selection), CancellationToken.None));
        var errors = await Task.WhenAll(
            checkpointAttempt, startAttempt, recoveryAttempt, suspendAttempt, rootRegistrationAttempt);
        Assert.All(errors, error => Assert.Equal("checkpoint_recovery_dispatch_held", error.Code));

        var item = Assert.Single(fixture.Plan.Items);
        var spawnIntent = new MafChildSpawnIntent(
            Guid.NewGuid(), _binding.Identity.SessionId, _binding.Identity.SessionId,
            fixture.Plan.Id, recovered.CheckpointRecovery!.DecisionStateVersion, item.WorkflowStepId,
            item.Id, _selection.Selection.ContextRevision, recovered.ExecutionFence, item);
        var spawnAuthority = new MafChildSpawnAuthorizationAuthority(
            _binding.Identity.ProjectId, _binding.Identity.RunId, _actor.Issuer, _actor.Subject,
            "tenant-1", CoordinationOwnerStore.HashSelection(_selection.Selection),
            _selection.Selection.ContextRevision, _selection.Selection.ProjectRevision,
            _selection.Selection.ProjectConfigurationRevision, _selection.Selection.PlatformRuntimeRevision,
            _selection.Authorization.MembershipRevision, 1, AgtPolicyProvider.ProviderId,
            AgtPolicyProvider.AdapterVersion.ToString(), AgtPolicyProvider.OptionsSchemaVersion, "policy-1");
        var spawnAuthorizations = new MafChildSpawnAuthorizationOwnerStore(
            _fixture.DataSource,
            new OrchestratorOptions(_actor.Issuer, "orchestrator", "https://projects.test/", "projects",
                "https://events.test/", "events", _schema),
            TimeProvider.System);
        var spawnGrant = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            spawnAuthorizations.IssueGrantAsync(spawnIntent, spawnAuthority, CancellationToken.None));
        Assert.Equal("checkpoint_recovery_dispatch_held", spawnGrant.Code);
        var spawnRead = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            spawnAuthorizations.ReadCurrentAsync(spawnIntent, spawnAuthority, CancellationToken.None));
        Assert.Equal("checkpoint_recovery_dispatch_held", spawnRead.Code);

        var retained = await current.ReadLatestAsync(CancellationToken.None);
        Assert.Equal(checkpoint.Info.CheckpointId, retained!.Info.CheckpointId);
        Assert.Equal(checkpoint.State.Revision, retained.State.Revision);
    }

    [Fact]
    public async Task ChildRecoveryRegistrationBarrierBlocksRootUntilEveryManifestMemberIsRegistered()
    {
        var fixture = await PrepareChildRecoveryAsync();
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var manifest = await fixture.Owner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, recovered.OperationId, CancellationToken.None);
        var error = Assert.Throws<CoordinationException>(() =>
            CheckpointRecoveryMemberBarrier.RequireAllRegistered(
                manifest, rootRegistered: true, new HashSet<string>(StringComparer.Ordinal)));
        Assert.Equal("checkpoint_recovery_registration_barrier", error.Code);
        CheckpointRecoveryMemberBarrier.RequireAllRegistered(
            manifest,
            rootRegistered: true,
            manifest.Members.Where(member => member.Disposition == "pendingUnsent")
                .Select(member => member.AssociationId).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ChildRecoveryRegistrationAuthorizationRejectsWrongManifestMember()
    {
        var fixture = await PrepareChildRecoveryAsync();
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var valid = await fixture.Owner.ReadCheckpointRecoveryRegistrationAuthorizationAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Child.SessionId,
            recovered.OperationId, CancellationToken.None);

        Assert.Equal(fixture.Child.SessionId, valid.Member!.SessionId);
        var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
            fixture.Owner.ReadCheckpointRecoveryRegistrationAuthorizationAsync(
                _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, "other-child",
                recovered.OperationId, CancellationToken.None));
        Assert.Equal("checkpoint_recovery_registration_stale", failure.Code);
    }

    [Fact]
    public async Task ChildRecoveryRootRegistrationReadUsesAuthorizationToCrossDispatchHold()
    {
        var fixture = await PrepareChildRecoveryAsync();
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var ordinaryRead = await Assert.ThrowsAsync<CoordinationException>(() =>
            fixture.Owner.ReadRuntimeOwnerStateForRegistrationAsync(
                _actor, _binding.Identity, "tenant-1", selectionHash, CancellationToken.None));
        Assert.Equal("checkpoint_recovery_dispatch_held", ordinaryRead.Code);

        var authorization = await fixture.Owner.ReadCheckpointRecoveryRegistrationAuthorizationAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, _binding.Identity.SessionId,
            recovered.OperationId, CancellationToken.None);
        var authorizedRead = await fixture.Owner.ReadRuntimeOwnerStateForRegistrationAsync(
            _actor, _binding.Identity, "tenant-1", selectionHash, CancellationToken.None, authorization);
        Assert.True(authorizedRead.IsCoordinatorRoot);
        Assert.Equal(recovered.ExecutionFence, authorizedRead.ExecutionFence);
    }

    [Fact]
    public async Task ChildRecoveryHoldReleasesOnlyAfterEveryManifestMemberIsReady()
    {
        var fixture = await PrepareChildRecoveryAsync();
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var status = recovered.CheckpointRecovery!;
        var manifest = await fixture.Owner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, recovered.OperationId, CancellationToken.None);
        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);
        var rootIntent = await starts.ReadIntentForStatusAsync(
            _binding.Identity.ProjectId, _binding.Identity.RunId, _binding.Identity.SessionId,
            CancellationToken.None);
        var unpersistedRootReadyIntent = rootIntent! with
        {
            ExecutionFence = status.ExecutionFence,
            State = "ready",
            CurrentStep = "ready",
            FailureCode = null,
            EnvironmentId = manifest.RootStartup.EnvironmentId,
            RuntimeInstanceId = manifest.RootStartup.RuntimeInstanceId
        };
        var readyStatus = status with
        {
            Phase = "ready",
            CurrentStep = "ready",
            ReasonCode = null,
            RecoveryMaterialMode = "nativeCache",
            DispatchHeld = false,
            RuntimeInstanceId = manifest.RootStartup.RuntimeInstanceId,
            RegistrationRevision = manifest.RootStartup.RegistrationRevision
        };

        var premature = await Assert.ThrowsAsync<CoordinationException>(() =>
            fixture.Owner.UpdateCheckpointRecoveryProgressAsync(
                _actor, _binding.Identity, recovered.OperationId, readyStatus,
                CancellationToken.None, unpersistedRootReadyIntent));
        Assert.Equal("checkpoint_recovery_members_not_ready", premature.Code);
        Assert.True(await IsChildRecoveryHeldAsync());

        var rootReadyIntent = await MarkRecoveryMemberReadyAsync(fixture, recovered, manifest);
        var currentReadyStatus = WithCurrentRootRegistration(readyStatus, rootReadyIntent);
        _ = await fixture.Owner.UpdateCheckpointRecoveryProgressAsync(
            _actor, _binding.Identity, recovered.OperationId, currentReadyStatus,
            CancellationToken.None, rootReadyIntent);
        Assert.False(await IsChildRecoveryHeldAsync());
    }

    [Theory]
    [InlineData("root", "placement")]
    [InlineData("root", "activation")]
    [InlineData("root", "readiness")]
    [InlineData("child", "placement")]
    [InlineData("child", "activation")]
    [InlineData("child", "readiness")]
    public async Task ChildRecoveryHoldRejectsEmptyReadinessEvidenceForRootOrChild(
        string target,
        string receiptName)
    {
        var fixture = await PrepareChildRecoveryAsync();
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var status = recovered.CheckpointRecovery! with
        {
            Phase = "ready",
            CurrentStep = "ready",
            ReasonCode = null,
            RecoveryMaterialMode = "nativeCache",
            DispatchHeld = false
        };
        var manifest = await fixture.Owner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, recovered.OperationId, CancellationToken.None);
        var rootReadyIntent = await MarkRecoveryMemberReadyAsync(fixture, recovered, manifest);
        status = WithCurrentRootRegistration(status, rootReadyIntent);
        var sessionId = target == "root" ? _binding.Identity.SessionId : fixture.Child.SessionId;
        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);
        var intent = await starts.ReadIntentForStatusAsync(
            _binding.Identity.ProjectId, _binding.Identity.RunId, sessionId, CancellationToken.None)
            ?? throw new InvalidOperationException("The startup intent is missing.");
        var webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var links = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            intent.ReceiptLinks, webOptions) ?? new(StringComparer.Ordinal);
        links[receiptName] = JsonSerializer.SerializeToElement(new { }, webOptions);
        _ = await fixture.Owner.PersistCheckpointRecoveryCleanupEvidenceAsync(
            intent,
            JsonSerializer.Serialize(links, webOptions),
            recovered.ExecutionFence,
            intent.BootstrapOperationId,
            intent.EnvironmentId,
            intent.RuntimeInstanceId,
            CancellationToken.None);

        var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
            fixture.Owner.UpdateCheckpointRecoveryProgressAsync(
                _actor, _binding.Identity, recovered.OperationId, status,
                CancellationToken.None, rootReadyIntent));
        Assert.Equal("checkpoint_recovery_members_not_ready", failure.Code);
        Assert.True(await IsChildRecoveryHeldAsync());
    }

    [Fact]
    public async Task ChildRecoveryMalformedManifestFailsClosedAndKeepsDispatchHold()
    {
        var fixture = await PrepareChildRecoveryAsync();
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            UPDATE "{_schema}".coordination_execution_operations
            SET progress = jsonb_set(progress, ARRAY['childManifest'], to_jsonb(@manifest::text), true)
            WHERE operation_id = @operation AND project_id = @project AND run_id = @run
            """, connection))
        {
            command.Parameters.AddWithValue("manifest", "{");
            command.Parameters.AddWithValue("operation", recovered.OperationId);
            command.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
            command.Parameters.AddWithValue("run", _binding.Identity.RunId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        Assert.True(await IsChildRecoveryHeldAsync());
    }

    private async Task<RuntimeRunStartIntent> MarkRecoveryMemberReadyAsync(
        ChildRecoveryFixture fixture,
        RunExecutionTransitionResult recovered,
        ChildCheckpointRecoveryManifest manifest)
    {
        var member = Assert.Single(manifest.Members.Where(item => item.Disposition == "pendingUnsent"));
        var registrations = new RuntimeRegistrationStore(_fixture.DataSource, _schema, TimeProvider.System);
        var rootPreviousRegistration = await registrations.ReadAsync(
            manifest.RootStartup.RuntimeInstanceId, CancellationToken.None)
            ?? throw new InvalidOperationException("The saved root registration is missing.");
        var rootOwner = RuntimeRunStartContinuation.CreateCheckpointRecoveryOwnerContext(
            rootPreviousRegistration.Binding, recovered.ExecutionFence);
        var root = await ReplaceRecoveryRuntimeAsync(
            registrations, recovered.OperationId, manifest.Root.SessionId,
            rootPreviousRegistration, manifest.RootStartup.Placement, rootOwner,
            "recovered-root-resource", "recovered-root-pod");
        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);
        var rootIntent = await starts.ReadIntentForStatusAsync(
            manifest.Root.ProjectId, manifest.Root.RunId, manifest.Root.SessionId, CancellationToken.None)
            ?? throw new InvalidOperationException("The root startup intent is missing.");
        var rootLinks = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            rootIntent.ReceiptLinks, ChildRecoveryJsonOptions) ?? new(StringComparer.Ordinal);
        rootLinks["registration"] = JsonSerializer.SerializeToElement(root.Registration, ChildRecoveryJsonOptions);
        rootLinks["placement"] = JsonSerializer.SerializeToElement(root.Placement, ChildRecoveryJsonOptions);
        var rootBootstrapId = RuntimeRunStartKeys.ReplacementOperationId(
            recovered.OperationId, "checkpoint-bootstrap");
        var rootConsumeId = RuntimeRunStartKeys.ReplacementOperationId(
            recovered.OperationId, "checkpoint-consume");
        var rootExchangeId = RuntimeRunStartKeys.ReplacementOperationId(
            recovered.OperationId, "checkpoint-exchange");
        var rootReadiness = CreateRecoveryReadiness(root.Registration, root.Placement);
        rootLinks["activation"] = JsonSerializer.SerializeToElement(rootReadiness.Activation, ChildRecoveryJsonOptions);
        rootLinks["readiness"] = JsonSerializer.SerializeToElement(rootReadiness.Readiness, ChildRecoveryJsonOptions);
        RuntimeRunStartContinuation.SetCheckpointRecoveryBootstrapEvidence(
            rootLinks,
            recovered.OperationId,
            JsonSerializer.SerializeToElement(
                CreateRecoveryBootstrapReceipt(root.Registration, rootBootstrapId), ChildRecoveryJsonOptions));
        rootIntent = await fixture.Owner.PersistCheckpointRecoveryCleanupEvidenceAsync(
            rootIntent,
            JsonSerializer.Serialize(rootLinks, ChildRecoveryJsonOptions),
            recovered.ExecutionFence,
            rootBootstrapId,
            root.Placement.EnvironmentId,
            root.Registration.RuntimeInstanceId,
            CancellationToken.None,
            currentStep: "ready",
            consumeOperationId: rootConsumeId,
            exchangeOperationId: rootExchangeId);

        var previousRegistration = await registrations.ReadAsync(
            member.Startup.RegistrationId!.Value, CancellationToken.None)
            ?? throw new InvalidOperationException("The saved child registration is missing.");
        var recoveryOwner = RuntimeRunStartContinuation.CreateCheckpointRecoveryOwnerContext(
            previousRegistration.Binding, recovered.ExecutionFence, member.Session.LogicalTurnOrdinal);
        var bootstrapId = RuntimeRunStartKeys.ReplacementOperationId(
            recovered.OperationId, member.Session.SessionId, "checkpoint-bootstrap");
        var child = await ReplaceRecoveryRuntimeAsync(
            registrations, recovered.OperationId, member.Session.SessionId,
            previousRegistration, member.Startup.Placement!, recoveryOwner,
            "recovered-child-resource", "recovered-child-pod");
        var intent = await starts
            .ReadIntentForStatusAsync(
                fixture.Child.ProjectId, fixture.Child.RunId, fixture.Child.SessionId, CancellationToken.None)
            ?? throw new InvalidOperationException("The child startup intent is missing.");
        var links = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            intent.ReceiptLinks, ChildRecoveryJsonOptions) ?? new(StringComparer.Ordinal);
        links["registration"] = JsonSerializer.SerializeToElement(child.Registration, ChildRecoveryJsonOptions);
        links["placement"] = JsonSerializer.SerializeToElement(child.Placement, ChildRecoveryJsonOptions);
        var childReadiness = CreateRecoveryReadiness(child.Registration, child.Placement);
        links["activation"] = JsonSerializer.SerializeToElement(childReadiness.Activation, ChildRecoveryJsonOptions);
        links["readiness"] = JsonSerializer.SerializeToElement(childReadiness.Readiness, ChildRecoveryJsonOptions);
        RuntimeRunStartContinuation.SetCheckpointRecoveryBootstrapEvidence(
            links,
            recovered.OperationId,
            JsonSerializer.SerializeToElement(
                CreateRecoveryBootstrapReceipt(child.Registration, bootstrapId), ChildRecoveryJsonOptions));
        var consumeId = RuntimeRunStartKeys.ReplacementOperationId(
            recovered.OperationId, member.Session.SessionId, "checkpoint-consume");
        var exchangeId = RuntimeRunStartKeys.ReplacementOperationId(
            recovered.OperationId, member.Session.SessionId, "checkpoint-exchange");
        _ = await fixture.Owner.PersistCheckpointRecoveryCleanupEvidenceAsync(
            intent,
            JsonSerializer.Serialize(links, ChildRecoveryJsonOptions),
            recovered.ExecutionFence,
            bootstrapId,
            child.Placement.EnvironmentId,
            child.Registration.RuntimeInstanceId,
            CancellationToken.None,
            currentStep: "ready",
            ready: true,
            consumeOperationId: consumeId,
            exchangeOperationId: exchangeId);
        return rootIntent;
    }

    private static CheckpointRecoveryStatus WithCurrentRootRegistration(
        CheckpointRecoveryStatus status,
        RuntimeRunStartIntent rootIntent)
    {
        using var links = JsonDocument.Parse(rootIntent.ReceiptLinks);
        var registration = links.RootElement.GetProperty("registration")
            .Deserialize<RuntimeRegistration>(ChildRecoveryJsonOptions)
            ?? throw new InvalidOperationException("The replacement root registration is missing.");
        if (registration.RuntimeInstanceId != rootIntent.RuntimeInstanceId)
            throw new InvalidOperationException("The root intent does not match its replacement registration.");
        return status with
        {
            RuntimeInstanceId = registration.RuntimeInstanceId,
            RegistrationRevision = registration.Revision
        };
    }

    private async Task<(RuntimeRegistration Registration, RuntimeRunStartPlacementReceipt Placement)>
        ReplaceRecoveryRuntimeAsync(
            RuntimeRegistrationStore registrations,
            Guid operationId,
            string sessionId,
            RuntimeRegistration previousRegistration,
            RuntimeRunStartPlacementReceipt previousPlacement,
            RuntimeOwnerContext owner,
            string resourceId,
            string podUid)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var binding = previousRegistration.Binding with
        {
            TurnId = owner.TurnId,
            ExecutionFence = owner.ExecutionFence,
            PlacementUid = resourceId,
            PlacementGeneration = previousRegistration.Binding.PlacementGeneration + 1,
            EnvironmentCurrentFencingGeneration =
                previousRegistration.Binding.EnvironmentCurrentFencingGeneration + 1,
            EnvironmentProviderFencingGeneration =
                previousRegistration.Binding.EnvironmentProviderFencingGeneration + 1
        };
        var registration = await registrations.ReplaceAsync(
            previousRegistration,
            RuntimeRunStartKeys.ReplacementOperationId(operationId, sessionId, "checkpoint-registration"),
            binding,
            expiresAt,
            CancellationToken.None);
        var bootstrapId = RuntimeRunStartKeys.ReplacementOperationId(
            operationId, sessionId, "checkpoint-bootstrap");
        var placement = previousPlacement with
        {
            SandboxIdempotencyKey = RuntimeRunStartKeys.SandboxIdempotencyKey(owner, bootstrapId),
            SandboxOperationId = Guid.NewGuid(),
            SandboxResource = new ProviderResourceRef(
                ProviderSeam.Sandbox, previousPlacement.SandboxResource.ProviderId,
                resourceId, previousPlacement.SandboxResource.Generation + 1),
            LeaseRevision = previousPlacement.LeaseRevision + 1,
            CurrentFencingGeneration = previousPlacement.CurrentFencingGeneration + 1,
            ProviderFencingGeneration = previousPlacement.ProviderFencingGeneration + 1,
            LeaseExpiresAt = expiresAt,
            PodUid = podUid
        };
        return (registration, placement);
    }

    private static RuntimeBootstrapDeliveryReceipt CreateRecoveryBootstrapReceipt(
        RuntimeRegistration registration,
        Guid bootstrapId)
    {
        var now = DateTimeOffset.UtcNow;
        return new RuntimeBootstrapDeliveryReceipt(
            bootstrapId, Guid.NewGuid(), registration.RuntimeInstanceId, registration.Revision,
            registration.Binding.PlacementUid, registration.Binding.PlacementGeneration,
            registration.Binding.ExecutionFence,
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes("{}")), now)
        {
            EnvironmentCurrentFencingGeneration =
                registration.Binding.EnvironmentCurrentFencingGeneration,
            EnvironmentProviderFencingGeneration =
                registration.Binding.EnvironmentProviderFencingGeneration
        };
    }

    private static (RuntimeHostReadinessReceipt Activation, EnvironmentRuntimeReadinessContext Readiness)
        CreateRecoveryReadiness(
            RuntimeRegistration registration,
            RuntimeRunStartPlacementReceipt placement)
    {
        var now = DateTimeOffset.UtcNow;
        var image = registration.Binding.Image!;
        var phases = Enum.GetValues<SandboxStartupPhase>().Select(phase =>
            new SandboxStartupPhaseObservation(
                phase, 1, now.AddSeconds(-10),
                phase == SandboxStartupPhase.ImageReady ? image.Digest : null,
                phase == SandboxStartupPhase.ImageReady ? image.CompressedPullBytes : null))
            .ToImmutableArray();
        var grant = new RuntimeGrantReceipt(
            Guid.NewGuid(), registration.RuntimeInstanceId, registration.Revision, 1,
            registration.Binding.ActorIssuer, RuntimeCredentialPurpose.Observe,
            registration.Binding.ObservationEndpoint, RuntimeCredentialState.Active,
            new string('d', 64), now.AddMinutes(30), now);
        var source = new SdkSessionFacts(
            registration.RuntimeInstanceId,
            RuntimeContractValidation.NativeSessionId(registration.Binding),
            "1.0.18", "1.0.97", registration.Binding.ModelSelectionReference!,
            "gpt-6.1-sol", new string('c', 64), 1, "hosted-copilot",
            SdkMeterSources.CopilotNanoAiu, registration.Binding.AcceptedSelectionHash,
            registration.Revision);
        var activation = new RuntimeHostReadinessReceipt(
            1, registration.RuntimeInstanceId, registration.Revision,
            registration.Binding.ExecutionFence, image, phases, grant)
        {
            Recovery = new RuntimeHostRecoveryReceipt(
                1, registration.RuntimeInstanceId, registration.Revision,
                registration.Binding.ExecutionFence, RuntimeHostRecoveryMode.JournalRebuild, source)
            {
                Journal = new RuntimeHostJournalRecoveryEvidence(1, new string('b', 64))
            }
        };
        var endpoint = new SandboxRuntimeHttpsEndpoint(
            new Uri("https://runtime.test:8443/"), "10.0.0.2", placement.PodUid!, "service-uid");
        var environmentPlacement = new EnvironmentRuntimeBootstrapContext(
            1, registration.Binding.TenantId, registration.Binding.ProjectId, registration.Binding.RunId,
            placement.EnvironmentId, placement.LifecycleGeneration, placement.CurrentFencingGeneration,
            placement.ProviderFencingGeneration, placement.LeaseRevision, placement.LeaseExpiresAt,
            placement.SandboxResource, new(Guid.NewGuid()), new("zone-a"),
            registration.Binding.ProfileId, registration.Binding.ConfigureEndpoint,
            registration.Binding.ObservationEndpoint)
        {
            Image = image,
            RuntimeEndpoint = endpoint
        };
        var providerPhases = phases.Where(phase => phase.Phase is
            SandboxStartupPhase.Scheduled or SandboxStartupPhase.Started or SandboxStartupPhase.ImageReady)
            .ToImmutableArray();
        var pod = new SandboxPodObservation(
            1, now, new EnvironmentGenerationFence(
                new EnvironmentOwnerIdentity(
                    registration.Binding.TenantId, registration.Binding.ProjectId,
                    registration.Binding.RunId, placement.EnvironmentId),
                placement.LifecycleGeneration),
            placement.SandboxResource, placement.ProviderFencingGeneration, placement.SandboxOperationId,
            new string('e', 64), "namespace", placement.SandboxResource.ResourceId,
            "sandbox-uid", "runtime-pod", placement.PodUid!, "42", 1, "Running",
            new("namespace", "workspace", placement.SandboxResource.ResourceId, "/workspace", false), true);
        var readiness = new EnvironmentRuntimeReadinessContext(
            1, environmentPlacement, now.AddMinutes(-1),
            new SandboxObservation(
                placement.SandboxResource, SandboxObservedState.Ready,
                placement.ProviderFencingGeneration, true, true, placement.NetworkPolicyGeneration,
                providerPhases, ProvisionOperationId: placement.SandboxOperationId)
            {
                RuntimeEndpoint = endpoint,
                PodObservation = pod
            },
            new SandboxStartupTimeBudgets(30, 30, 30, 30, 30, 150), "/workspace");
        return (activation, readiness);
    }

    private async Task<bool> IsChildRecoveryHeldAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var held = await CheckpointRecoveryHold.IsHeldAsync(
            connection, transaction, $"\"{_schema}\"",
            _binding.Identity.ProjectId, _binding.Identity.RunId, CancellationToken.None);
        await transaction.RollbackAsync();
        return held;
    }

    [Fact]
    public async Task ChildRecoveryWinsRunLockBeforeWaitingCheckpointWriter()
    {
        var fixture = await PrepareChildRecoveryAsync();
        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand($"""
            SELECT execution_fence FROM "{_schema}".accepted_runs
            WHERE project_id = @project AND run_id = @run FOR UPDATE
            """, blocker, transaction))
        {
            command.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
            command.Parameters.AddWithValue("run", _binding.Identity.RunId);
            Assert.Equal(_binding.ExecutionFence, (long)(await command.ExecuteScalarAsync())!);
        }

        var recovery = fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        await WaitForRunLockWaitersAsync(blocker.ProcessID, 1);
        var append = _executionStore.AppendAsync(
            "racing-checkpoint", fixture.Source.Info,
            fixture.Source.State with { Revision = fixture.Source.State.Revision + 1 },
            CancellationToken.None);
        await WaitForRunLockWaitersAsync(blocker.ProcessID, 2);
        await transaction.CommitAsync();

        var accepted = await recovery;
        var failure = await Assert.ThrowsAsync<CoordinationException>(() => append);
        Assert.Contains(failure.Code, new[]
        {
            "checkpoint_recovery_dispatch_held", "checkpoint_execution_fence_stale"
        });
        var current = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null),
            _binding with { ExecutionFence = accepted.ExecutionFence });
        var retained = await current.ReadLatestAsync(CancellationToken.None);
        Assert.Equal(accepted.CheckpointRecovery!.RecoveryCheckpointId, retained!.Info.CheckpointId);
        Assert.Equal(fixture.Source.Info.CheckpointId, accepted.CheckpointRecovery!.SourceCheckpointId);
    }

    [Fact]
    public async Task BusyRegistrationRejectsAdmissionWithoutChangingFenceAndAllowsRetry()
    {
        var fixture = await PrepareChildRecoveryAsync();
        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand($"""
            SELECT current_revision FROM "{_schema}".runtime_registration_heads
            WHERE runtime_instance_id = @runtime FOR UPDATE
            """, blocker, transaction))
        {
            command.Parameters.AddWithValue("runtime", fixture.ChildIntent.RuntimeInstanceId!.Value);
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }

        var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
            fixture.Owner.RecoverRunExecutionAsync(
                _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
                CancellationToken.None, _selection, _executionStore));
        Assert.Equal("checkpoint_recovery_registration_busy", failure.Code);
        var status = await fixture.Owner.ReadOwnerRunStatusAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, CancellationToken.None);
        Assert.Equal(_binding.ExecutionFence, status.ExecutionFence);
        Assert.Equal(fixture.Source.Info.CheckpointId,
            (await _executionStore.ReadLatestAsync(CancellationToken.None))!.Info.CheckpointId);

        await transaction.CommitAsync();
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        Assert.Equal(_binding.ExecutionFence + 1, recovered.ExecutionFence);
        Assert.False(recovered.IsDuplicate);
    }

    [Fact]
    public async Task HeldRecoveryRejectsLateMessageAdmissionWithoutWakingRoot()
    {
        var fixture = await PrepareChildRecoveryAsync();
        using var payload = JsonDocument.Parse("""{"text":"Input is needed."}""");
        var message = await fixture.Owner.SendMessageAsync(
            _actor, fixture.Child,
            new CoordinationMessageRequest(
                _binding.Identity.SessionId, "late-child-message", AddressedMessageDeliveryMode.Enqueue,
                AddressedMessagePurpose.NeedsInput, AddressedMessageKind.Text, payload.RootElement),
            CancellationToken.None);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"""
            SELECT thread_id, status FROM "{_schema}".coordination_messages
            WHERE project_id = @project AND message_id = @message
            """, connection);
        query.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
        query.Parameters.AddWithValue("message", message.OwnerMessageId);
        Guid thread;
        await using (var reader = await query.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            thread = reader.GetGuid(0);
            Assert.Equal("pending", reader.GetString(1));
        }

        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var rootBefore = await fixture.Owner.ReadSessionStatusAsync(
            _actor, _binding.Identity, CancellationToken.None);
        var receipt = new MessageAdmissionReceipt(
            message.OwnerMessageId, Guid.NewGuid(), thread, 1, AddressedMessageStatus.Accepted,
            null, AddressedMessagePurpose.NeedsInput, fixture.Child, _binding.Identity,
            _binding.ExecutionFence, _binding.ExecutionFence);
        var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
            fixture.Owner.AdmitDeliveryAsync(
                _actor, new DeliveryIngressRequest(message.OwnerMessageId, receipt), CancellationToken.None));
        Assert.Equal("checkpoint_recovery_dispatch_held", failure.Code);
        var rootAfter = await fixture.Owner.ReadSessionStatusAsync(
            _actor, _binding.Identity, CancellationToken.None);
        Assert.Equal(recovered.ExecutionFence, rootAfter.ExecutionFence);
        Assert.Equal(rootBefore.StateVersion, rootAfter.StateVersion);
        await using var after = await query.ExecuteReaderAsync();
        Assert.True(await after.ReadAsync());
        Assert.Equal("pending", after.GetString(1));
    }

    private async Task WaitForRunLockWaitersAsync(int blockerPid, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var observer = await _fixture.DataSource.OpenConnectionAsync(timeout.Token);
        // A later row-lock waiter queues behind the first waiter's tuple lock, so PostgreSQL reports
        // the first waiter as its blocker. Count waiters that the blocker holds directly or through that queue.
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_activity waiter
            WHERE waiter.datname = current_database() AND waiter.wait_event_type = 'Lock'
              AND waiter.query LIKE @pattern
              AND (@blocker = ANY(pg_blocking_pids(waiter.pid)) OR EXISTS (
                  SELECT 1 FROM unnest(pg_blocking_pids(waiter.pid)) AS ahead(pid)
                  WHERE @blocker = ANY(pg_blocking_pids(ahead.pid))))
            """, observer);
        command.Parameters.AddWithValue("blocker", blockerPid);
        command.Parameters.AddWithValue("pattern", $"%{_schema}%accepted_runs%");
        while ((long)(await command.ExecuteScalarAsync(timeout.Token))! < count)
            await Task.Delay(25, timeout.Token);
    }

    [Fact]
    public async Task CompletedBuildTestHistoryRejectsChildRecoveryBeforeAdmission()
    {
        var fixture = await PrepareChildRecoveryAsync(includeBuildTest: true);
        var before = await fixture.Owner.ReadOwnerRunStatusAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, CancellationToken.None);
        var rootBefore = await fixture.Owner.ReadSessionStatusAsync(
            _actor, _binding.Identity, CancellationToken.None);
        var childBefore = await fixture.Owner.ReadSessionStatusAsync(
            _actor, fixture.Child, CancellationToken.None);
        var decisionBefore = await fixture.Decisions.ReadCurrentAsync(
            _actor, _binding.Identity, _selection, CancellationToken.None);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var outbox = new NpgsqlCommand(
            $"""SELECT count(*) FROM "{_schema}".outbox_events""", connection);
        var outboxBefore = (long)(await outbox.ExecuteScalarAsync())!;
        var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
            fixture.Owner.RecoverRunExecutionAsync(
                _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
                CancellationToken.None, _selection, _executionStore));
        Assert.Equal("checkpoint_recovery_boundary_unsupported", failure.Code);
        Assert.Equal("build_test_history_requires_effect_recovery", failure.Reason);
        var buildTest = Assert.Single(fixture.Source.State.BuildTestIntents);
        Assert.Equal(buildTest.Key, failure.StepId);
        Assert.Equal(buildTest.Value.OperationId, failure.OperationId);
        var after = await fixture.Owner.ReadOwnerRunStatusAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, CancellationToken.None);
        Assert.Equal(before.ExecutionFence, after.ExecutionFence);
        Assert.Equal(before.StateVersion, after.StateVersion);
        Assert.Equal(rootBefore.StateVersion,
            (await fixture.Owner.ReadSessionStatusAsync(
                _actor, _binding.Identity, CancellationToken.None)).StateVersion);
        Assert.Equal(childBefore.StateVersion,
            (await fixture.Owner.ReadSessionStatusAsync(
                _actor, fixture.Child, CancellationToken.None)).StateVersion);
        Assert.Equal(decisionBefore.StateVersion,
            (await fixture.Decisions.ReadCurrentAsync(
                _actor, _binding.Identity, _selection, CancellationToken.None)).StateVersion);
        Assert.Equal(outboxBefore, (long)(await outbox.ExecuteScalarAsync())!);
        var retained = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.Equal(fixture.Source.Info.CheckpointId, retained!.Info.CheckpointId);
        Assert.Equal(fixture.Source.PayloadHash, retained.PayloadHash);
        await using var count = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".coordination_execution_operations
            WHERE project_id = @project AND run_id = @run AND operation_kind = 'recovery'
            """, connection);
        count.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
        count.Parameters.AddWithValue("run", _binding.Identity.RunId);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    private async Task<ChildRecoveryFixture> PrepareChildRecoveryAsync(
        bool includeBuildTest = false, string nativeState = "absent")
    {
        // Runtime registrations accept only opaque identifiers, so this fixture pins a model reference without '/'.
        const string modelReference = "model:provider-alpha";
        _reviewSelectionContext = await InitializeReviewSelectionAsync(modelReference);
        var steps = includeBuildTest
            ? new[]
            {
                WorkflowTestData.Platform("build-test", 0, [], WorkflowPlatformGate.BuildTest) with
                {
                    BuildTestCommand = MafBuildTestCheckpointTests.Intent().Command
                },
                WorkflowTestData.Open("implement", 1, ["build-test"])
            }
            : [WorkflowTestData.Open("implement", 0, [])];
        var result = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(WorkflowTestData.Definition(WorkflowDefinitionOrigin.BuiltIn, steps)),
            WorkflowTestData.Plan(WorkflowTestData.Item(
                "first", modelSelection: modelReference, providerId: ReviewSandboxProviderId)),
            _reviewSelectionContext);
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        var plan = result.Value!;
        var decisions = CreateReviewDecisionOwner();
        var initialized = await decisions.InitializeRootAsync(
            _actor, _binding.Identity, _selection, CancellationToken.None);
        var outcome = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            initialized.State, new("outcome-1", "Implement the change.", "The change is reviewed.",
                "Only the requested scope.", "Source is available.", []),
            "outcome-request", _actor.Subject);
        var acceptedOutcome = outcome.State!.ApplyGateAnswer(new(
            "outcome-request", _actor.Subject, _binding.ExecutionFence,
            CoordinatorGateChoices.Approve, null)).State;
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            acceptedOutcome, new(plan.Workflow.Definition, [plan.Workflow.Definition]), null, null, null);
        var proposed = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State, plan.Plan, _reviewSelectionContext, "plan-request", _actor.Subject);
        Assert.True(proposed.IsSuccess);
        var confirmed = proposed.State!.ApplyGateAnswer(new(
            "plan-request", _actor.Subject, _binding.ExecutionFence,
            CoordinatorGateChoices.Approve, null)).State;
        var decision = await decisions.PersistTransitionAsync(
            _actor, _binding.Identity, _selection, initialized.StateVersion, "confirm-plan", "confirm-plan",
            "work_plan.confirm", CoordinatorDecisionOwnerStore.ComputeCommandHash(plan.Plan),
            confirmed, true, [], _reviewSelectionContext, null, plan.Plan, CancellationToken.None);
        var owner = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, decisions);
        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var rootRuntime = new RuntimeOwnerContext(
            1, _actor.Issuer, _actor.Subject, "tenant-1", _binding.Identity.ProjectId, _binding.Identity.RunId,
            _binding.Identity.SessionId, CoordinationEndpoints.CoordinatorRuntimeAgentId, modelReference,
            "root-turn", 1, 1, 1, _selection.Selection.ContextRevision, selectionHash.ToLowerInvariant(),
            _binding.ExecutionFence, 0, 1, decision.StateVersion);
        var rootIntent = await starts.EnsureIntentAsync(rootRuntime, CancellationToken.None);
        var registrationStore = new RuntimeRegistrationStore(_fixture.DataSource, _schema, TimeProvider.System);
        var rootRegistration = await registrationStore.RegisterAsync(new RuntimeBinding(
            _actor.Issuer, _actor.Subject, "tenant-1", _binding.Identity.ProjectId, _binding.Identity.RunId,
            _binding.Identity.SessionId, CoordinationEndpoints.CoordinatorRuntimeAgentId, "root-turn", 1, 1, 1,
            _selection.Selection.ContextRevision, selectionHash.ToLowerInvariant(), _binding.ExecutionFence,
            "root-environment", "root-resource", 1, "profile-1",
            new("https://runtime.test/runtime/v1/configure"), new("https://runtime.test/observation"))
        {
            ModelSelectionReference = modelReference,
            ModelSourceMode = ModelSourceMode.HostedCopilot,
            PlacementProviderId = ReviewSandboxProviderId,
            Image = new SandboxImageIdentity("sha256:" + new string('a', 64), "linux/amd64", 1024),
            EnvironmentLifecycleGeneration = 1,
            EnvironmentLeaseRevision = 1,
            EnvironmentCurrentFencingGeneration = 1,
            EnvironmentProviderFencingGeneration = 1
        }, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        var rootRuntimeId = rootRegistration.RuntimeInstanceId;
        var rootPlacement = new RuntimeRunStartPlacementReceipt(
            1, "root-environment", "profile-1", "root-volume", "root-sandbox-key",
            1, 0, 1, Guid.NewGuid(),
            new ProviderResourceRef(ProviderSeam.Sandbox, ReviewSandboxProviderId, "root-resource", 1),
            1, 1, 1, 1, rootRegistration.ExpiresAt) { PodUid = "root-pod" };
        var rootBootstrap = new RuntimeBootstrapDeliveryReceipt(
            rootIntent.BootstrapOperationId, Guid.NewGuid(), rootRuntimeId, rootRegistration.Revision,
            rootRegistration.Binding.PlacementUid, rootRegistration.Binding.PlacementGeneration,
            rootRegistration.Binding.ExecutionFence, new string('d', 64), DateTimeOffset.UtcNow)
        {
            EnvironmentCurrentFencingGeneration = 1,
            EnvironmentProviderFencingGeneration = 1
        };
        _ = await starts.UpdateAsync(rootIntent, rootIntent.RowVersion, "ready", "ready", null,
            "root-environment", rootRuntimeId,
            new { registration = rootRegistration, placement = rootPlacement, bootstrap = rootBootstrap },
            CancellationToken.None);

        var rootStatus = await owner.ReadSessionStatusAsync(_actor, _binding.Identity, CancellationToken.None);
        var presenting = await owner.AdvanceTurnBoundaryAsync(
            _actor, _binding.Identity, _binding.ExecutionFence, rootStatus.StateVersion, CancellationToken.None);
        _ = await owner.CompleteTurnBoundaryAsync(
            _actor, _binding.Identity, _binding.ExecutionFence, rootStatus.StateVersion,
            presenting.StateVersion, presentedMessage: null, cancellationToken: CancellationToken.None);
        if (includeBuildTest)
        {
            var initial = await _executionStore.AppendAsync(
                "initial-child-recovery", null,
                new MafExecutionCheckpoint(1, plan.Plan.Id, decision.StateVersion, MafExecutionProgress.Empty),
                CancellationToken.None);
            _ = await AppendSuccessfulBuildTestAsync(initial, plan);
        }

        var item = Assert.Single(plan.Plan.Items);
        var childId = MafExecutionPlanner.CreateChildSessionId(_binding.Identity, plan.Plan.Id, item.Id);
        const string prompt = "Execute the approved work item.";
        var dispatch = new MafExecutionDispatchIntent(
            item.Id, childId, MafExecutionPlanner.CreateDispatchMessageId(_binding.Identity, plan.Plan.Id, item.Id),
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt)))
        {
            EgressReceipt = new RuntimeEgressVerificationReceipt(
                1, new EnvironmentGenerationFence(
                    new EnvironmentOwnerIdentity("tenant-1", _binding.Identity.ProjectId,
                        _binding.Identity.RunId, "root-environment"), 1),
                1, _selection.Selection.ContextRevision, new string('b', 64))
        };
        var association = new ConfirmedWorkPlanItemAssociation(item.Id, decision.StateVersion, selectionHash);
        var spawned = await owner.SpawnSessionAsync(
            _actor, _binding.Identity,
            new SpawnSessionRequest(childId, CoordinationSessionKind.ChildWork, "spawn-child", item.Task,
                WorkPlanItemId: item.Id),
            100, 32, CancellationToken.None, association,
            persistSpawnedInTransaction: async (connection, transaction, child, token) =>
            {
                await starts.PersistChildIntentInTransactionAsync(
                    connection, transaction, "tenant-1", _binding.Identity.ProjectId, _binding.Identity.RunId,
                    selectionHash, _binding.ExecutionFence,
                    new RuntimeChildStartBinding(
                        _binding.Identity.SessionId, childId, plan.Plan.Id, item.Id, item.Id,
                        item.WorkflowStepId, item.AgentId, item.ModelSelectionReference,
                        _actor.Issuer, _actor.Subject, _selection.Selection.ContextRevision,
                        decision.StateVersion, child.Node.StateVersion, child.CommandId,
                        dispatch.MessageId, dispatch.PromptHash), token);
                var latest = await _executionStore.ReadLatestInTransactionAsync(connection, transaction, token);
                var next = CoordinationEndpoints.PrepareRunningCheckpoint(
                    latest, plan.Plan.Id, decision.StateVersion, item.Id, null, dispatch);
                if (next is not null)
                    _ = await _executionStore.AppendInTransactionAsync(
                        connection, transaction, Guid.NewGuid().ToString("N"), latest?.Info, next, token);
            });
        var childIntent = await starts.ReadIntentForStatusAsync(
            _binding.Identity.ProjectId, _binding.Identity.RunId, childId, CancellationToken.None);
        Assert.NotNull(childIntent);
        var registration = await registrationStore.RegisterAsync(new RuntimeBinding(
                _actor.Issuer, _actor.Subject, "tenant-1", _binding.Identity.ProjectId, _binding.Identity.RunId,
                childId, item.AgentId, "child-turn", 1, 1, 1, _selection.Selection.ContextRevision,
                selectionHash.ToLowerInvariant(), _binding.ExecutionFence, "child-environment",
                "child-resource", 1, "profile-1", new("https://runtime.test/runtime/v1/configure"),
                new("https://runtime.test/observation"))
            {
                WorkflowStepId = item.WorkflowStepId,
                ModelSelectionReference = item.ModelSelectionReference,
                ModelSourceMode = ModelSourceMode.HostedCopilot,
                PlacementProviderId = ReviewSandboxProviderId,
                Image = new SandboxImageIdentity("sha256:" + new string('a', 64), "linux/amd64", 1024),
                EnvironmentLifecycleGeneration = 1,
                EnvironmentLeaseRevision = 1,
                EnvironmentCurrentFencingGeneration = 1,
                EnvironmentProviderFencingGeneration = 1
            }, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        var placement = new RuntimeRunStartPlacementReceipt(
            1, "child-environment", "profile-1", "child-volume", "child-sandbox-key",
            1, 0, 1, Guid.NewGuid(),
            new ProviderResourceRef(ProviderSeam.Sandbox, ReviewSandboxProviderId, "child-resource", 1),
            1, 1, 1, 1, registration.ExpiresAt) { PodUid = "child-pod" };
        var bootstrap = new RuntimeBootstrapDeliveryReceipt(
            childIntent.BootstrapOperationId, Guid.NewGuid(), registration.RuntimeInstanceId,
            registration.Revision, registration.Binding.PlacementUid, registration.Binding.PlacementGeneration,
            registration.Binding.ExecutionFence, new string('c', 64), DateTimeOffset.UtcNow)
        {
            EnvironmentCurrentFencingGeneration = 1,
            EnvironmentProviderFencingGeneration = 1
        };
        childIntent = await starts.UpdateAsync(
            childIntent, childIntent.RowVersion, "ready", "ready", null, placement.EnvironmentId,
            registration.RuntimeInstanceId,
            new
            {
                childStart = CoordinationRuntimeRunStartStore.ReadChildBinding(childIntent),
                registration,
                placement,
                bootstrap
            }, CancellationToken.None);
        var childStatus = await owner.ReadSessionStatusAsync(_actor, spawned.Node.Identity, CancellationToken.None);
        var childPresenting = await owner.AdvanceTurnBoundaryAsync(
            _actor, spawned.Node.Identity, _binding.ExecutionFence, childStatus.StateVersion, CancellationToken.None);
        _ = await owner.CompleteTurnBoundaryAsync(
            _actor, spawned.Node.Identity, _binding.ExecutionFence, childStatus.StateVersion,
            childPresenting.StateVersion, presentedMessage: null, cancellationToken: CancellationToken.None);
        var source = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(source);
        if (nativeState is "prepared" or "sending")
            await PrepareChildNativeAsync(
                registration, dispatch, prompt, source, nativeState == "sending");
        var run = await owner.ReadOwnerRunStatusAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, CancellationToken.None);
        var request = new RecoverRunExecutionRequest(
            _binding.ExecutionFence, run.StateVersion, "child-recovery", "operator_recovery", "lost-host")
        {
            CheckpointRecovery = new CheckpointRecoveryRequest(
                source.Info.CheckpointId, source.State.Revision, decision.StateVersion,
                rootRuntimeId, 1, selectionHash.ToLowerInvariant())
        };
        return new ChildRecoveryFixture(owner, decisions, spawned.Node.Identity, childIntent, plan.Plan, source, request);
    }

    private async Task PrepareChildNativeAsync(
        RuntimeRegistration registration,
        MafExecutionDispatchIntent dispatch,
        string prompt,
        MafExecutionCheckpointSnapshot checkpoint,
        bool begin)
    {
        var options = new OrchestratorOptions(
            _actor.Issuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", _schema);
        var store = new RuntimeUsageSourceStore(_fixture.DataSource, options, TimeProvider.System);
        var grant = new RuntimeGrantReceipt(
            Guid.NewGuid(), registration.RuntimeInstanceId, registration.Revision, 1, _actor.Issuer,
            RuntimeCredentialPurpose.Observe, registration.Binding.ObservationEndpoint,
            RuntimeCredentialState.Active, new string('a', 64), registration.ExpiresAt, DateTimeOffset.UtcNow);
        var source = new SdkSessionFacts(
            registration.RuntimeInstanceId, RuntimeContractValidation.NativeSessionId(registration.Binding),
            "1.0.18", "1.0.97", registration.Binding.ModelSelectionReference!,
            "gpt-6.1-sol", new string('c', 64), 1, "hosted-copilot", SdkMeterSources.CopilotNanoAiu,
            registration.Binding.AcceptedSelectionHash, registration.Revision);
        await store.ExecuteLockedAsync(registration.RuntimeInstanceId, async (connection, transaction, token) =>
        {
            var receipt = await store.RegisterWithinTransactionAsync(
                connection, transaction, registration, grant, source, token);
            await transaction.CommitAsync(token);
            return receipt;
        }, CancellationToken.None);
        var message = new RuntimeA2ASendRequest(new(
            "message", dispatch.MessageId, source.SdkSessionId, "user", [new("text", prompt)],
            new(new(1, registration, grant.GrantId, grant.Revision, RuntimeCredentialPurpose.Observe),
                AddressedMessageDeliveryMode.Enqueue)));
        static Task<RuntimeUsageCostSnapshotReceipt> NoCost(SdkSessionFacts _, CancellationToken __) =>
            throw new InvalidOperationException("This fixture must not send a cost request.");
        await store.ExecuteLockedAsync(registration.RuntimeInstanceId, async (connection, transaction, token) =>
        {
            var prepared = await store.PrepareNativeTurnWithinTransactionAsync(
                connection, transaction, _binding.Identity, checkpoint, dispatch, registration,
                message, NoCost, token);
            await transaction.CommitAsync(token);
            return prepared;
        }, CancellationToken.None);
        if (!begin)
            return;
        await store.ExecuteLockedAsync(registration.RuntimeInstanceId, async (connection, transaction, token) =>
        {
            var result = await store.BeginNativeTurnWithinTransactionAsync(
                connection, transaction, registration, grant,
                new(new(grant.GrantId, registration.RuntimeInstanceId, grant.Revision,
                    RuntimeCredentialPurpose.Observe, grant.Audience, grant.ConfigurationHash,
                    "controlled-store-only", grant.ExpiresAt, Guid.NewGuid()), message, source),
                NoCost, token);
            await transaction.CommitAsync(token);
            return result;
        }, CancellationToken.None);
    }

    private sealed record ChildRecoveryFixture(
        CoordinationOwnerStore Owner,
        CoordinatorDecisionOwnerStore Decisions,
        SessionIdentity Child,
        RuntimeRunStartIntent ChildIntent,
        WorkPlan Plan,
        MafExecutionCheckpointSnapshot Source,
        RecoverRunExecutionRequest Request);
}
