using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class MafCheckpointStorePostgresTests
{
    private const string CompletedUnjoinedResult = "The approved work is complete.";

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

        var (ready, rootReadyIntent) = await ContinueCheckpointRecoveryAsync(
            fixture, recovered, manifest);
        Assert.Equal("ready", ready.CheckpointRecovery!.Phase);
        Assert.False(await IsChildRecoveryHeldAsync());
        _ = await fixture.Owner.UpdateCheckpointRecoveryProgressAsync(
            _actor, _binding.Identity, recovered.OperationId, ready.CheckpointRecovery,
            CancellationToken.None, rootReadyIntent);
        Assert.False(await IsChildRecoveryHeldAsync());
    }

    [Fact]
    public async Task ChildRecoverySupersessionPreservesR1ToR2CheckpointLineageAndReleasesHoldAfterReady()
    {
        var fixture = await PrepareChildRecoveryAsync();
        var first = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var firstManifest = await fixture.Owner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, first.OperationId, CancellationToken.None);
        var firstCheckpointStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null),
            _binding with { ExecutionFence = first.ExecutionFence });
        var firstCheckpoint = await firstCheckpointStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(firstCheckpoint);

        var (blocked, firstRootIntent) = await ContinueCheckpointRecoveryAsync(
            fixture, first, firstManifest, failRootActivation: true);
        var firstRootRegistration = ReadIntentReceipt<RuntimeRegistration>(firstRootIntent, "registration");
        Assert.Equal("blocked", blocked.CheckpointRecovery!.Phase);
        Assert.Equal("activation", blocked.CheckpointRecovery.CurrentStep);
        Assert.Equal("checkpoint_recovery_host_bootstrap_spent", blocked.CheckpointRecovery.ReasonCode);
        Assert.True(await IsChildRecoveryHeldAsync());

        var restartedOwner = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, fixture.Decisions);
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var secondRequest = fixture.Request with
        {
            ExecutionFence = first.ExecutionFence,
            ExpectedRunStateVersion = blocked.RunStateVersion,
            IdempotencyKey = "child-recovery-successor",
            CheckpointRecovery = new CheckpointRecoveryRequest(
                firstCheckpoint.Info.CheckpointId,
                firstCheckpoint.State.Revision,
                blocked.CheckpointRecovery.DecisionStateVersion,
                firstRootRegistration.RuntimeInstanceId,
                firstRootRegistration.Revision,
                selectionHash.ToLowerInvariant())
        };
        var second = await restartedOwner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, secondRequest,
            CancellationToken.None, _selection, firstCheckpointStore);
        Assert.Equal(first.ExecutionFence + 1, second.ExecutionFence);
        Assert.Equal(first.OperationId, second.CheckpointRecovery!.SupersedesOperationId);
        Assert.NotNull(second.CheckpointRecovery.SupersessionReceiptId);
        var secondCheckpointStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null),
            _binding with { ExecutionFence = second.ExecutionFence });
        var secondCheckpoint = await secondCheckpointStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(secondCheckpoint);
        var secondLineage = Assert.IsType<MafCheckpointRecoveryLineage>(secondCheckpoint.State.RecoveryLineage);
        Assert.Equal(firstCheckpoint.Info.CheckpointId, secondLineage.SourceCheckpointId);
        Assert.Equal(first.ExecutionFence, secondLineage.SourceExecutionFence);
        Assert.Equal(second.ExecutionFence, secondLineage.RecoveryExecutionFence);
        Assert.Equal(first.OperationId, secondLineage.SupersedesOperationId);
        Assert.Equal(second.CheckpointRecovery.SupersessionReceiptId, secondLineage.SupersessionReceiptId);
        Assert.Equal(second.CheckpointRecovery.SupersessionPayloadHash, secondLineage.SupersessionPayloadHash);
        Assert.True(await IsChildRecoveryHeldAsync());

        var superseded = await restartedOwner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        Assert.Equal("blocked", superseded.CheckpointRecovery!.Phase);
        Assert.Equal(second.OperationId, superseded.CheckpointRecovery!.SupersededByOperationId);
        Assert.NotNull(superseded.CheckpointRecovery.SupersededByReceiptId);
        Assert.True(superseded.CheckpointRecovery.DispatchHeld);
        Assert.True(await IsChildRecoveryHeldAsync());

        var secondManifest = await fixture.Owner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, second.OperationId, CancellationToken.None);
        var (ready, _) = await ContinueCheckpointRecoveryAsync(fixture, second, secondManifest);
        Assert.Equal("ready", ready.CheckpointRecovery!.Phase);
        Assert.False(ready.CheckpointRecovery.DispatchHeld);
        Assert.False(await IsChildRecoveryHeldAsync());
        var finalReplay = await restartedOwner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        Assert.Equal("blocked", finalReplay.CheckpointRecovery!.Phase);
        Assert.Equal(second.OperationId, finalReplay.CheckpointRecovery!.SupersededByOperationId);
        Assert.False(finalReplay.CheckpointRecovery.DispatchHeld);
    }

    [Fact]
    public async Task ChildRecoverySupersessionRebindsPreparedNativeDispatchWithoutChangingOrigin()
    {
        var fixture = await PrepareChildRecoveryAsync(nativeState: "prepared");
        var first = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var firstManifest = await fixture.Owner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, first.OperationId, CancellationToken.None);
        var checkpointStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null),
            _binding with { ExecutionFence = first.ExecutionFence });
        var checkpoint = await checkpointStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(checkpoint);

        var (blocked, rootIntent) = await ContinueCheckpointRecoveryAsync(
            fixture, first, firstManifest, failRootActivation: true);
        Assert.Equal("blocked", blocked.CheckpointRecovery!.Phase);
        Assert.True(blocked.CheckpointRecovery.DispatchHeld);
        Assert.True(await IsChildRecoveryHeldAsync());
        var rootRegistration = ReadIntentReceipt<RuntimeRegistration>(rootIntent, "registration");
        var request = fixture.Request with
        {
            ExecutionFence = first.ExecutionFence,
            ExpectedRunStateVersion = blocked.RunStateVersion,
            IdempotencyKey = "prepared-child-recovery-successor",
            CheckpointRecovery = new CheckpointRecoveryRequest(
                checkpoint.Info.CheckpointId,
                checkpoint.State.Revision,
                blocked.CheckpointRecovery!.DecisionStateVersion,
                rootRegistration.RuntimeInstanceId,
                rootRegistration.Revision,
                CoordinationOwnerStore.HashSelection(_selection.Selection).ToLowerInvariant())
        };
        var restartedOwner = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, fixture.Decisions);
        var second = await restartedOwner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, request,
            CancellationToken.None, _selection, checkpointStore);
        Assert.Equal(first.ExecutionFence + 1, second.ExecutionFence);
        Assert.Equal(first.OperationId, second.CheckpointRecovery!.SupersedesOperationId);
        var secondManifest = await restartedOwner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, second.OperationId, CancellationToken.None);
        var (ready, _) = await ContinueCheckpointRecoveryAsync(fixture, second, secondManifest);
        Assert.Equal("ready", ready.CheckpointRecovery!.Phase);
        Assert.False(ready.CheckpointRecovery.DispatchHeld);
        Assert.False(await IsChildRecoveryHeldAsync());

        var firstReplay = await restartedOwner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, checkpointStore);
        Assert.Equal("blocked", firstReplay.CheckpointRecovery!.Phase);
        Assert.Equal(second.OperationId, firstReplay.CheckpointRecovery.SupersededByOperationId);
        Assert.False(firstReplay.CheckpointRecovery.DispatchHeld);
        var current = await restartedOwner.ReadOwnerRunStatusAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, CancellationToken.None);
        Assert.Equal(second.ExecutionFence, current.ExecutionFence);

        var originalMember = Assert.Single(firstManifest.Members);
        var original = Assert.IsType<ChildRecoveryNativeOrigin>(originalMember.NativeOrigin);
        var savedMember = Assert.Single(secondManifest.Members);
        Assert.Equal(originalMember.Session.SessionId, savedMember.Session.SessionId);
        Assert.Equal(original.DispatchId, savedMember.MessageId);
        Assert.Equal(originalMember.AssociationId, savedMember.AssociationId);
        Assert.Equal(original, savedMember.NativeOrigin);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var currentOrigin = await ChildCheckpointRecoveryCensus.ReadNativeOriginAsync(
            connection, transaction, _schema, _binding.Identity, originalMember.Session,
            originalMember.Startup.Binding, CoordinationOwnerStore.HashSelection(_selection.Selection),
            CancellationToken.None);
        Assert.Equal(original, currentOrigin);
        var rebinds = await new RuntimeExecutionEvidenceLedgerStore(_schema).ReadNativeChildRebindsAsync(
            connection, transaction, _binding.Identity.ProjectId, _binding.Identity.RunId,
            original.DispatchId, CancellationToken.None);
        var rebind = Assert.Single(rebinds);
        Assert.Equal(second.OperationId, rebind.RecoveryOperationId);
        Assert.Equal(original.DispatchId, rebind.DispatchId);
        Assert.Equal(original.RequestHash, rebind.OriginalRequestHash);
        Assert.Equal("origin", rebind.PriorAuthorityKey);
    }

    [Fact]
    public async Task ChildRecoveryNativeRebindReadyCommitRollsBackAndReplaysAfterRestart()
    {
        var fixture = await PrepareChildRecoveryAsync(nativeState: "prepared");
        var first = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var firstManifest = await fixture.Owner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, first.OperationId, CancellationToken.None);
        var firstCheckpointStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null),
            _binding with { ExecutionFence = first.ExecutionFence });
        var firstCheckpoint = await firstCheckpointStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(firstCheckpoint);
        var (blocked, rootIntent) = await ContinueCheckpointRecoveryAsync(
            fixture, first, firstManifest, failRootActivation: true);
        Assert.Equal("blocked", blocked.CheckpointRecovery!.Phase);
        var previousRoot = ReadIntentReceipt<RuntimeRegistration>(rootIntent, "registration");
        var successorRequest = fixture.Request with
        {
            ExecutionFence = first.ExecutionFence,
            ExpectedRunStateVersion = blocked.RunStateVersion,
            IdempotencyKey = "native-rebind-rollback-successor",
            CheckpointRecovery = new CheckpointRecoveryRequest(
                firstCheckpoint.Info.CheckpointId,
                firstCheckpoint.State.Revision,
                blocked.CheckpointRecovery.DecisionStateVersion,
                previousRoot.RuntimeInstanceId,
                previousRoot.Revision,
                CoordinationOwnerStore.HashSelection(_selection.Selection).ToLowerInvariant())
        };
        var restartOwner = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, fixture.Decisions);
        var successor = await restartOwner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, successorRequest,
            CancellationToken.None, _selection, firstCheckpointStore);
        var successorManifest = await restartOwner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, successor.OperationId, CancellationToken.None);

        await CreateReadyCommitFailureTriggerAsync();
        try
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                ContinueCheckpointRecoveryAsync(fixture, successor, successorManifest));
            Assert.Contains("native_child_rebind_ready_commit_fault", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DropReadyCommitFailureTriggerAsync();
        }

        Assert.True(await IsChildRecoveryHeldAsync());
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var receipts = await new RuntimeExecutionEvidenceLedgerStore(_schema).ReadNativeChildRebindsAsync(
                connection, transaction, _binding.Identity.ProjectId, _binding.Identity.RunId,
                fixture.Dispatch.MessageId, CancellationToken.None);
            Assert.Empty(receipts);
            await transaction.CommitAsync();
        }

        var (ready, _) = await ContinueCheckpointRecoveryAsync(fixture, successor, successorManifest);
        Assert.Equal("ready", ready.CheckpointRecovery!.Phase);
        Assert.False(await IsChildRecoveryHeldAsync());
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var receipts = await new RuntimeExecutionEvidenceLedgerStore(_schema).ReadNativeChildRebindsAsync(
                connection, transaction, _binding.Identity.ProjectId, _binding.Identity.RunId,
                fixture.Dispatch.MessageId, CancellationToken.None);
            var stored = Assert.Single(receipts);
            var replay = await new RuntimeExecutionEvidenceLedgerStore(_schema).AppendNativeChildRebindAsync(
                connection, transaction, stored, CancellationToken.None);
            Assert.Equal(stored.CanonicalPayloadHash, replay.CanonicalPayloadHash);
            Assert.Single(await new RuntimeExecutionEvidenceLedgerStore(_schema).ReadNativeChildRebindsAsync(
                connection, transaction, _binding.Identity.ProjectId, _binding.Identity.RunId,
                fixture.Dispatch.MessageId, CancellationToken.None));
            await transaction.CommitAsync();
        }
    }

    [Fact]
    public async Task PreparedNativeRecoveryRequiresMigrationBeforeFenceAdmission()
    {
        var fixture = await PrepareChildRecoveryAsync(nativeState: "prepared");
        await RemoveNativeChildRebindMigrationAsync();
        var before = await fixture.Owner.ReadOwnerRunStatusAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, CancellationToken.None);

        var pending = await Assert.ThrowsAsync<CoordinationException>(() =>
            fixture.Owner.RecoverRunExecutionAsync(
                _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
                CancellationToken.None, _selection, _executionStore));

        Assert.Equal("database_update_pending", pending.Code);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, pending.StatusCode);
        var after = await fixture.Owner.ReadOwnerRunStatusAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, CancellationToken.None);
        Assert.Equal(before, after);
        Assert.False(await IsChildRecoveryHeldAsync());
    }

    [Fact]
    public async Task NativeChildRebindCompletesOriginalMessageThroughReplacementAccounting()
    {
        var context = await PrepareReadyNativeRebindAsync();
        var replacement = await ReadReplacementChildRegistrationAsync(context.Fixture.Child);
        await PrepareChildNativeAsync(
            replacement, context.Fixture.Dispatch, context.Fixture.Prompt, context.Checkpoint,
            begin: true, complete: true, result: context.Fixture.Result);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT dispatch_state, owner_revision FROM "{_schema}".maf_execution_dispatches
            WHERE dispatch_id = @dispatch
            """, connection);
        command.Parameters.AddWithValue("dispatch", context.Fixture.Dispatch.MessageId);
            string savedState;
            long savedRevision;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                savedState = reader.GetString(0);
                savedRevision = reader.GetInt64(1);
            }
            Assert.Equal("completed", savedState);
            Assert.Equal(3, savedRevision);
            await using var transaction = await connection.BeginTransactionAsync();
            var receipts = await new RuntimeExecutionEvidenceLedgerStore(_schema).ReadNativeChildRebindsAsync(
            connection, transaction, _binding.Identity.ProjectId, _binding.Identity.RunId,
            context.Fixture.Dispatch.MessageId, CancellationToken.None);
        Assert.Single(receipts);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task NativeChildRebindAcceptsOnlyVerifiedCheckpointDescendants()
    {
        var context = await PrepareReadyNativeRebindAsync();
        var replacement = await ReadReplacementChildRegistrationAsync(context.Fixture.Child);

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => PrepareChildNativeAsync(
            replacement, context.Fixture.Dispatch, context.Fixture.Prompt, context.Fixture.Source,
            begin: false));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => PrepareChildNativeAsync(
            replacement, context.Fixture.Dispatch with { PromptHash = new string('f', 64) },
            context.Fixture.Prompt, context.Checkpoint, begin: false));

        await context.CheckpointStore.AppendAsync(
            "native-rebind-descendant",
            context.Checkpoint.Info,
            context.Checkpoint.State with { Revision = context.Checkpoint.State.Revision + 1 },
            CancellationToken.None);
        var descendant = await context.CheckpointStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(descendant);
        Assert.NotEqual(context.Checkpoint.Info.CheckpointId, descendant.Info.CheckpointId);
        await PrepareChildNativeAsync(
            replacement, context.Fixture.Dispatch, context.Fixture.Prompt, descendant,
            begin: false);
        var current = await context.CheckpointStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(descendant.Info.CheckpointId, current.Info.CheckpointId);
    }

    [Fact]
    public async Task NativeChildRebindSerializesWithBeginAndSupersession()
    {
        var fixture = await PrepareChildRecoveryAsync(nativeState: "prepared");
        var original = fixture.ChildRegistration;
        var store = new RuntimeUsageSourceStore(_fixture.DataSource, new OrchestratorOptions(
            _actor.Issuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", _schema), TimeProvider.System);
        var grant = new RuntimeGrantReceipt(
            Guid.NewGuid(), original.RuntimeInstanceId, original.Revision, 1, _actor.Issuer,
            RuntimeCredentialPurpose.Observe, original.Binding.ObservationEndpoint,
            RuntimeCredentialState.Active, new string('a', 64), original.ExpiresAt, DateTimeOffset.UtcNow);
        var source = new SdkSessionFacts(
            original.RuntimeInstanceId, RuntimeContractValidation.NativeSessionId(original.Binding),
            "1.0.18", "1.0.97", original.Binding.ModelSelectionReference!,
            "gpt-6.1-sol", new string('c', 64), 1, "hosted-copilot", SdkMeterSources.CopilotNanoAiu,
            original.Binding.AcceptedSelectionHash, original.Revision);
        var request = new RuntimeA2ASendRequest(new(
            "message", fixture.Dispatch.MessageId, source.SdkSessionId, "user", [new("text", fixture.Prompt)],
            new(new(1, original, grant.GrantId, grant.Revision, RuntimeCredentialPurpose.Observe),
                AddressedMessageDeliveryMode.Enqueue)));
        await store.ExecuteLockedAsync(original.RuntimeInstanceId, async (connection, transaction, token) =>
        {
            _ = await store.RegisterWithinTransactionAsync(connection, transaction, original, grant, source, token);
            await transaction.CommitAsync(token);
            return true;
        }, CancellationToken.None);
        static Task<RuntimeUsageCostSnapshotReceipt> NoCost(SdkSessionFacts _, CancellationToken __) =>
            throw new InvalidOperationException("This fixture must not send a cost request.");
        var beginTask = store.ExecuteLockedAsync(original.RuntimeInstanceId, async (connection, transaction, token) =>
        {
            var result = await store.BeginNativeTurnWithinTransactionAsync(
                connection, transaction, original, grant,
                new(new(grant.GrantId, original.RuntimeInstanceId, grant.Revision,
                    RuntimeCredentialPurpose.Observe, grant.Audience, grant.ConfigurationHash,
                    "controlled-store-only", grant.ExpiresAt, Guid.NewGuid()), request, source),
                NoCost, token);
            await transaction.CommitAsync(token);
            return result;
        }, CancellationToken.None);
        var recoveryOwner = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, fixture.Decisions);
        var firstRecoveryTask = recoveryOwner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId,
            fixture.Request with { IdempotencyKey = $"native-rebind-race-a-{Guid.NewGuid():N}" },
            CancellationToken.None, _selection, _executionStore);
        var secondRecoveryTask = recoveryOwner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId,
            fixture.Request with { IdempotencyKey = $"native-rebind-race-b-{Guid.NewGuid():N}" },
            CancellationToken.None, _selection, _executionStore);
        var outcomes = await Task.WhenAll(
            CaptureAsync(beginTask), CaptureAsync(firstRecoveryTask), CaptureAsync(secondRecoveryTask));
        Assert.Contains(outcomes, outcome => outcome is not null);
        Assert.Equal(1, outcomes.Skip(1).Count(outcome => outcome is null));
        var saved = await ReadNativeOriginAsync(fixture);
        Assert.True(saved.State is "sending" or "prepared");
        if (saved.State == "sending")
        {
            Assert.Equal(2, saved.OwnerRevision);
            Assert.Equal(0, await ReadNativeChildRebindCountAsync(fixture));
        }
        else
        {
            Assert.True(await IsChildRecoveryHeldAsync());
            Assert.Equal(1, saved.OwnerRevision);
        }
    }

    [Fact]
    public async Task NativeChildRebindPreservesReservationsAndCreditGuard()
    {
        var fixture = await PrepareChildRecoveryAsync(nativeState: "prepared");
        var dispatchesBefore = await ReadDispatchReservationCountAsync();
        await SeedCreditGuardAsync();
        var guardBefore = await ReadCreditGuardSnapshotAsync();
        var context = await PrepareReadyNativeRebindAsync(fixture);
        var dispatchesAfter = await ReadDispatchReservationCountAsync();
        Assert.Equal(dispatchesBefore, dispatchesAfter);
        Assert.Equal(1, dispatchesAfter);
        Assert.NotNull(guardBefore);
        var guardAfter = await ReadCreditGuardSnapshotAsync();
        Assert.NotNull(guardAfter);
        Assert.Equal(guardBefore with
        {
            OwnerRevision = guardBefore.OwnerRevision + 2,
            ExecutionFence = context.Ready.ExecutionFence
        }, guardAfter);
        var replacement = await ReadReplacementChildRegistrationAsync(fixture.Child);
        Assert.Equal(context.Ready.ExecutionFence, replacement.Binding.ExecutionFence);
        Assert.Equal(fixture.Dispatch.MessageId, context.Fixture.Dispatch.MessageId);
    }

    [Fact]
    public async Task ReboundNativeDispatchObservationKeepsOriginAndShowsEffectiveBinding()
    {
        var context = await PrepareReadyNativeRebindAsync();
        var replacement = await ReadReplacementChildRegistrationAsync(context.Fixture.Child);
        var observation = await new RuntimeUsageSourceStore(
            _fixture.DataSource, new OrchestratorOptions(
                _actor.Issuer, "orchestrator", "https://projects.test/", "projects",
                "https://events.test/", "events", _schema), TimeProvider.System)
            .ReadObservationEvidenceAsync(replacement, _binding.Identity.SessionId, CancellationToken.None);
        var dispatch = Assert.Single(observation.Dispatches.Items);
        Assert.Equal(context.Fixture.Dispatch.MessageId, dispatch.DispatchId);
        var rebind = Assert.IsType<RuntimeObservationNativeRebind>(dispatch.NativeRebind);
        Assert.Equal(context.Fixture.ChildRegistration.RuntimeInstanceId, rebind.Origin.RuntimeInstanceId);
        Assert.Equal(replacement.RuntimeInstanceId, rebind.Effective.RuntimeInstanceId);
        Assert.Equal(context.Ready.OperationId, rebind.RecoveryOperationId);
        Assert.NotEqual(rebind.Origin.RuntimeInstanceId, rebind.Effective.RuntimeInstanceId);
    }

    [Fact]
    public async Task NativeChildRebindChainsAcrossReadyRecoveries()
    {
        var context = await PrepareReadyNativeRebindAsync();
        var run = await context.Fixture.Owner.ReadOwnerRunStatusAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, CancellationToken.None);
        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);
        var rootIntent = await starts.ReadIntentForStatusAsync(
            _binding.Identity.ProjectId, _binding.Identity.RunId, _binding.Identity.SessionId,
            CancellationToken.None);
        Assert.NotNull(rootIntent);
        var rootRegistration = ReadIntentReceipt<RuntimeRegistration>(rootIntent, "registration");
        var source = await context.CheckpointStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(source);
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var nextRequest = new RecoverRunExecutionRequest(
            run.ExecutionFence,
            run.StateVersion,
            $"native-rebind-chain-{Guid.NewGuid():N}",
            "operator_recovery",
            "test")
        {
            CheckpointRecovery = new CheckpointRecoveryRequest(
                source.Info.CheckpointId,
                source.State.Revision,
                source.State.DecisionStateVersion,
                rootRegistration.RuntimeInstanceId,
                rootRegistration.Revision,
                selectionHash.ToLowerInvariant())
        };
        var next = await context.Fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, nextRequest,
            CancellationToken.None, _selection, context.CheckpointStore);
        var manifest = await context.Fixture.Owner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, next.OperationId, CancellationToken.None);
        var (ready, _) = await ContinueCheckpointRecoveryAsync(context.Fixture, next, manifest);
        Assert.Equal("ready", ready.CheckpointRecovery!.Phase);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var receipts = await new RuntimeExecutionEvidenceLedgerStore(_schema).ReadNativeChildRebindsAsync(
            connection, transaction, _binding.Identity.ProjectId, _binding.Identity.RunId,
            context.Fixture.Dispatch.MessageId, CancellationToken.None);
        Assert.Equal(2, receipts.Length);
        Assert.Equal(context.Ready.OperationId, receipts[0].RecoveryOperationId);
        Assert.Equal(next.OperationId, receipts[1].RecoveryOperationId);
        Assert.Equal(receipts[0].ReceiptId, receipts[1].PriorReceiptId);
        Assert.Equal(receipts[0].CanonicalPayloadHash, receipts[1].PriorReceiptHash);
        Assert.Equal(receipts[0].OriginFingerprint, receipts[1].OriginFingerprint);
        await transaction.CommitAsync();
    }

    private async Task<ReadyNativeRebindContext> PrepareReadyNativeRebindAsync(
        ChildRecoveryFixture? fixture = null)
    {
        fixture ??= await PrepareChildRecoveryAsync(nativeState: "prepared");
        var first = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var firstManifest = await fixture.Owner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, first.OperationId, CancellationToken.None);
        var firstCheckpointStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null),
            _binding with { ExecutionFence = first.ExecutionFence });
        var firstCheckpoint = await firstCheckpointStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(firstCheckpoint);
        var (blocked, rootIntent) = await ContinueCheckpointRecoveryAsync(
            fixture, first, firstManifest, failRootActivation: true);
        Assert.Equal("blocked", blocked.CheckpointRecovery!.Phase);
        var previousRoot = ReadIntentReceipt<RuntimeRegistration>(rootIntent, "registration");
        var successorRequest = fixture.Request with
        {
            ExecutionFence = first.ExecutionFence,
            ExpectedRunStateVersion = blocked.RunStateVersion,
            IdempotencyKey = $"native-rebind-successor-{Guid.NewGuid():N}",
            CheckpointRecovery = new CheckpointRecoveryRequest(
                firstCheckpoint.Info.CheckpointId,
                firstCheckpoint.State.Revision,
                blocked.CheckpointRecovery.DecisionStateVersion,
                previousRoot.RuntimeInstanceId,
                previousRoot.Revision,
                CoordinationOwnerStore.HashSelection(_selection.Selection).ToLowerInvariant())
        };
        var restartOwner = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, fixture.Decisions);
        var successor = await restartOwner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, successorRequest,
            CancellationToken.None, _selection, firstCheckpointStore);
        var successorManifest = await restartOwner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, successor.OperationId, CancellationToken.None);
        var (ready, _) = await ContinueCheckpointRecoveryAsync(fixture, successor, successorManifest);
        Assert.Equal("ready", ready.CheckpointRecovery!.Phase);
        var checkpointStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null),
            _binding with { ExecutionFence = ready.ExecutionFence });
        var checkpoint = await checkpointStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(checkpoint);
        return new(fixture, ready, successorManifest, checkpointStore, checkpoint, restartOwner);
    }

    private async Task<RuntimeRegistration> ReadReplacementChildRegistrationAsync(SessionIdentity child)
    {
        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);
        var intent = await starts.ReadIntentForStatusAsync(
            child.ProjectId, child.RunId, child.SessionId, CancellationToken.None);
        Assert.NotNull(intent);
        return ReadIntentReceipt<RuntimeRegistration>(intent, "registration");
    }

    private async Task<NativeDispatchState> ReadNativeOriginAsync(ChildRecoveryFixture fixture)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT dispatch_state, owner_revision FROM "{_schema}".maf_execution_dispatches
            WHERE dispatch_id = @dispatch
            """, connection);
        command.Parameters.AddWithValue("dispatch", fixture.Dispatch.MessageId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetString(0), reader.GetInt64(1));
    }

    private async Task<int> ReadNativeChildRebindCountAsync(ChildRecoveryFixture fixture)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var receipts = await new RuntimeExecutionEvidenceLedgerStore(_schema).ReadNativeChildRebindsAsync(
            connection, transaction, _binding.Identity.ProjectId, _binding.Identity.RunId,
            fixture.Dispatch.MessageId, CancellationToken.None);
        await transaction.CommitAsync();
        return receipts.Length;
    }

    private async Task<long> ReadDispatchReservationCountAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".maf_execution_dispatches
            WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
        command.Parameters.AddWithValue("run", _binding.Identity.RunId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task SeedCreditGuardAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".maf_execution_run_guards
                (project_id, run_id, root_session_id, tenant_id, accepted_selection_hash,
                 execution_fence, owner_revision, copilot_soft_credit_limit, copilot_hard_credit_limit,
                 cost_rate_scope_identity, copilot_aic_floor, accounted_through_event_position,
                 usage_witness_digest, soft_warning_emitted, hard_cap_exhausted)
            VALUES
                (@project, @run, @root, @tenant, @selection, @fence, 7, 3, 5,
                 'scope-1', 4, 3, @digest, true, true)
            """, connection);
        command.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
        command.Parameters.AddWithValue("run", _binding.Identity.RunId);
        command.Parameters.AddWithValue("root", _binding.Identity.SessionId);
        command.Parameters.AddWithValue("tenant", "tenant-1");
        command.Parameters.AddWithValue("selection",
            CoordinationOwnerStore.HashSelection(_selection.Selection));
        command.Parameters.AddWithValue("fence", _binding.ExecutionFence);
        command.Parameters.AddWithValue("digest", new string('a', 64));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<CreditGuardSnapshot?> ReadCreditGuardSnapshotAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT owner_revision, execution_fence, copilot_soft_credit_limit,
                   copilot_hard_credit_limit, cost_rate_scope_identity, cost_unit, cost_scope,
                   copilot_aic_floor, accounted_through_event_position, usage_witness_digest,
                   soft_warning_emitted, hard_cap_exhausted
            FROM "{_schema}".maf_execution_run_guards
            WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
        command.Parameters.AddWithValue("run", _binding.Identity.RunId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        return new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetDecimal(2),
            reader.IsDBNull(3) ? null : reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9).Trim(),
            reader.GetBoolean(10),
            reader.GetBoolean(11));
    }

    private sealed record CreditGuardSnapshot(
        long OwnerRevision,
        long ExecutionFence,
        decimal? SoftLimit,
        decimal? HardLimit,
        string? CostRateScopeIdentity,
        string CostUnit,
        string CostScope,
        decimal? Floor,
        long? AccountedThroughEventPosition,
        string? UsageWitnessDigest,
        bool SoftWarningEmitted,
        bool HardCapExhausted);

    private async Task CreateReadyCommitFailureTriggerAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE OR REPLACE FUNCTION "{_schema}".native_child_rebind_ready_commit_fault()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.result -> 'checkpointRecovery' ->> 'phase' = 'ready' THEN
                    RAISE EXCEPTION 'native_child_rebind_ready_commit_fault';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER native_child_rebind_ready_commit_fault
            BEFORE UPDATE OF result ON "{_schema}".coordination_execution_operations
            FOR EACH ROW EXECUTE FUNCTION "{_schema}".native_child_rebind_ready_commit_fault();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropReadyCommitFailureTriggerAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            DROP TRIGGER IF EXISTS native_child_rebind_ready_commit_fault
                ON "{_schema}".coordination_execution_operations;
            DROP FUNCTION IF EXISTS "{_schema}".native_child_rebind_ready_commit_fault();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RemoveNativeChildRebindMigrationAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            DROP INDEX "{_schema}".uq_native_child_rebind_destination;
            DROP INDEX "{_schema}".uq_native_child_rebind_prior;
            ALTER TABLE "{_schema}".maf_runtime_evidence_receipts
                DROP CONSTRAINT maf_runtime_evidence_native_child_rebind_fields_check;
            ALTER TABLE "{_schema}".maf_runtime_evidence_receipts
                DROP CONSTRAINT maf_runtime_evidence_receipts_receipt_kind_check;
            ALTER TABLE "{_schema}".maf_runtime_evidence_receipts
                ADD CONSTRAINT maf_runtime_evidence_receipts_receipt_kind_check CHECK (
                    receipt_kind IN (
                        'numeric-limit-denial',
                        'replacement-comparison',
                        'numeric-limit-native-no-send',
                        'native-root-turn-completion',
                        'native-root-turn-replay',
                        'checkpoint-recovery-supersession'));
            DELETE FROM "{_schema}".coordination_schema_migrations WHERE version = 51;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [Fact]
    public async Task ChildRecoveryImportsVerifiedCompletedUnjoinedResultAndReleasesHold()
    {
        var state = await PrepareCompletedUnjoinedImportAsync();
        var member = Assert.Single(state.Manifest.Members);
        var material = state.Material;
        Assert.NotEqual(member.AssociationHash, member.WorkItemBindingSha256);

        var (imported, _) = await ContinueCheckpointRecoveryAsync(
            state.Fixture, state.Recovered, state.Manifest, completedUnjoinedMaterial: material);

        Assert.Equal("ready", imported.CheckpointRecovery!.Phase);
        Assert.False(imported.CheckpointRecovery.DispatchHeld);
        Assert.False(await IsChildRecoveryHeldAsync());
        var child = await state.Fixture.Owner.ReadSessionStatusAsync(
            _actor, state.Fixture.Child, CancellationToken.None);
        Assert.Equal(CoordinationLifecycleState.Completed, child.Lifecycle);
        Assert.Equal(state.Recovered.ExecutionFence, child.ExecutionFence);
        var checkpoint = await state.CheckpointStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.NotEqual(state.Recovered.CheckpointRecovery!.RecoveryCheckpointId, checkpoint.Info.CheckpointId);
        Assert.Equal(material.Result, checkpoint.State.Results[member.AssociationId]);
        Assert.False(checkpoint.State.PendingDispatches.ContainsKey(member.AssociationId));
        Assert.Equal(MafExecutionTaskStatus.Succeeded,
            checkpoint.State.Progress.WorkItems[member.AssociationId]);
        Assert.Equal(member.MessageId, checkpoint.State.ChildTurnReceipts[member.AssociationId].RequestId);
    }

    [Theory]
    [InlineData("forged result", "runtime_native_turn_accounting_invalid")]
    [InlineData("bad\u0001result", "checkpoint_recovery_result_material_invalid")]
    [InlineData("", "events_session_material_invalid")]
    public async Task ChildRecoveryRejectsMismatchedOrMalformedCompletedUnjoinedResultAndKeepsHold(
        string result,
        string expectedReason)
    {
        var state = await PrepareCompletedUnjoinedImportAsync();
        var member = Assert.Single(state.Manifest.Members);
        var invalidMaterial = CreateCompletedUnjoinedMaterial(state.Fixture, result);
        var (failed, _) = await ContinueCheckpointRecoveryAsync(
            state.Fixture, state.Recovered, state.Manifest,
            completedUnjoinedMaterial: invalidMaterial);

        Assert.NotEqual("ready", failed.CheckpointRecovery!.Phase);
        Assert.Equal(expectedReason, failed.CheckpointRecovery.ReasonCode);
        Assert.True(await IsChildRecoveryHeldAsync());
        var checkpoint = await state.CheckpointStore.ReadLatestAsync(CancellationToken.None);
        Assert.Equal(state.Recovered.CheckpointRecovery!.RecoveryCheckpointId, checkpoint!.Info.CheckpointId);
        Assert.False(checkpoint.State.Results.ContainsKey(member.AssociationId));
    }

    [Fact]
    public async Task ChildRecoveryCompletedUnjoinedImportRetryIsIdempotent()
    {
        var state = await PrepareCompletedUnjoinedImportAsync();
        var (first, _) = await ContinueCheckpointRecoveryAsync(
            state.Fixture, state.Recovered, state.Manifest,
            completedUnjoinedMaterial: state.Material);
        var firstCheckpoint = await state.CheckpointStore.ReadLatestAsync(CancellationToken.None);
        var (replay, _) = await ContinueCheckpointRecoveryAsync(
            state.Fixture, first, state.Manifest,
            completedUnjoinedMaterial: state.Material);
        var replayCheckpoint = await state.CheckpointStore.ReadLatestAsync(CancellationToken.None);

        Assert.Equal("ready", first.CheckpointRecovery!.Phase);
        Assert.Equal(first.CheckpointRecovery, replay.CheckpointRecovery);
        Assert.Equal(firstCheckpoint!.Info.CheckpointId, replayCheckpoint!.Info.CheckpointId);
        Assert.Equal(firstCheckpoint.PayloadHash, replayCheckpoint.PayloadHash);
        Assert.Single(replayCheckpoint.State.ChildTurnReceipts);
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
        var manifest = await fixture.Owner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, recovered.OperationId, CancellationToken.None);
        var sessionId = target == "root" ? _binding.Identity.SessionId : fixture.Child.SessionId;
        var (failed, _) = await ContinueCheckpointRecoveryAsync(
            fixture, recovered, manifest, emptyEvidenceSessionId: sessionId,
            emptyEvidenceReceiptName: receiptName);
        Assert.NotEqual("ready", failed.CheckpointRecovery!.Phase);
        Assert.NotNull(failed.CheckpointRecovery.ReasonCode);
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

    private async Task<(RunExecutionTransitionResult Result, RuntimeRunStartIntent RootIntent)>
        ContinueCheckpointRecoveryAsync(
            ChildRecoveryFixture fixture,
            RunExecutionTransitionResult recovered,
            ChildCheckpointRecoveryManifest manifest,
            bool failRootActivation = false,
            string? emptyEvidenceSessionId = null,
            string? emptyEvidenceReceiptName = null,
            ChildRecoveryCompletedUnjoinedMaterial? completedUnjoinedMaterial = null)
    {
        var starts = new CoordinationRuntimeRunStartStore(_fixture.DataSource, _schema);
        var rootIntent = await starts.ReadIntentForStatusAsync(
            manifest.Root.ProjectId, manifest.Root.RunId, manifest.Root.SessionId, CancellationToken.None)
            ?? throw new InvalidOperationException("The root startup intent is missing.");
        var registrationStore = new RuntimeRegistrationStore(_fixture.DataSource, _schema, TimeProvider.System);
        var initial = new Dictionary<string, RecoveryRuntimeSeed>(StringComparer.Ordinal)
        {
            [manifest.Root.SessionId] = new(
                await registrationStore.ReadAsync(manifest.RootStartup.RuntimeInstanceId, CancellationToken.None)
                    ?? throw new InvalidOperationException("The saved root registration is missing."),
                manifest.RootStartup.Placement,
                manifest.RootStartup.BootstrapOperationId)
        };
        foreach (var member in manifest.Members.Where(member => member.Startup.RegistrationId is not null))
        {
            initial.Add(member.Session.SessionId, new(
                await registrationStore.ReadAsync(member.Startup.RegistrationId!.Value, CancellationToken.None)
                    ?? throw new InvalidOperationException("The saved child registration is missing."),
                member.Startup.Placement
                    ?? throw new InvalidOperationException("The saved child placement is missing."),
                member.Startup.BootstrapOperationId));
        }

        var options = new OrchestratorOptions(
            _actor.Issuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", _schema);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        const string bearer = "recovery-test-token";
        using var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new RecoveryOwnerAuthentication(expiresAt))
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new(new ClaimsIdentity(
            [
                new Claim("sub", _actor.Subject),
                new Claim("iss", _actor.Issuer),
                new Claim("aud", options.Audience),
                new Claim("aud", options.ProjectsAudience),
                new Claim("aud", options.EventsAudience),
                new Claim("project_id", manifest.Root.ProjectId),
                new Claim("run_id", manifest.Root.RunId),
                new Claim("scope", "api.read projects.orchestrator")
            ], "checkpoint-recovery-test"))
        };
        context.Request.Headers.Authorization = $"Bearer {bearer}";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        var runtimeActor = new RuntimeActorAuthorization(
            new SecretCredential(bearer, expiresAt), "tenant-1");
        using var projectsHttp = new HttpClient(new ReviewSelectionAuthorityHandler(
            _selection.Selection, _actor.Issuer, _actor.Subject));
        var projects = new ProjectsRunSelectionClient(
            projectsHttp, options, new ReviewNoSnapshotResolver());
        var contexts = CreateReviewSelectionContextStore();
        var checkpoints = new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null);
        async Task<RuntimeOwnerContext> ReadOwnerAsync(
            string sessionId, Guid operationId, CancellationToken cancellationToken)
        {
            var authorization = await fixture.Owner.ReadCheckpointRecoveryRegistrationAuthorizationAsync(
                _actor, manifest.Root.ProjectId, manifest.Root.RunId, sessionId, operationId,
                cancellationToken);
            return await CoordinationEndpoints.ReadRuntimeOwnerContextCoreAsync(
                manifest.Root.ProjectId, manifest.Root.RunId, sessionId, context, options, projects,
                fixture.Owner, fixture.Decisions, contexts, checkpoints, cancellationToken,
                allowCoordinatorRoot: true, checkpointRecoveryRegistration: authorization);
        }
        var peer = new RecoveryRuntimePeer(
            initial, ReadOwnerAsync,
            failRootActivation ? manifest.Root.SessionId : null,
            emptyEvidenceSessionId,
            emptyEvidenceReceiptName);
        using var environmentHttp = new HttpClient(peer);
        using var brokerHttp = new HttpClient(new RecoveryBrokerPeer(registrationStore, peer));
        var registrationOwner = new RuntimeRegistrationOwner(
            options, projects, fixture.Owner, fixture.Decisions, contexts, checkpoints,
            registrationStore,
            new RuntimeEnvironmentContextClient(environmentHttp, new(new Uri("https://environment.test/"))),
            TimeProvider.System);
        var clients = new RecoveryRuntimeHttpClientFactory(environmentHttp, brokerHttp);
        using var eventsHttp = completedUnjoinedMaterial is null
            ? null
            : new HttpClient(new RecoveryEventsPeer(
                fixture.Child, completedUnjoinedMaterial));
        var events = eventsHttp is null
            ? null
            : new EventsAddressedMessageClient(
                eventsHttp, options, new HttpContextAccessor { HttpContext = context });
        var usageSources = completedUnjoinedMaterial is null
            ? null
            : new RuntimeUsageSourceStore(_fixture.DataSource, options, TimeProvider.System);
        var continuation = new RuntimeRunStartContinuation(
            starts, registrationOwner,
            new RuntimeEnvironmentContextClient(environmentHttp, new(new Uri("https://environment.test/"))),
            new RuntimeRunStartOptions(new Uri("https://environment.test/"), new Uri("https://broker.test/")),
            clients, TimeProvider.System, events, usageSources);
        var checkpointStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null),
            _binding with { ExecutionFence = recovered.ExecutionFence });
        static Task NoGate(CancellationToken _) => Task.CompletedTask;

        var servedSelection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, manifest.Root.ProjectId, manifest.Root.RunId, CancellationToken.None);
        Assert.Equal(
            CoordinationOwnerStore.HashSelection(_selection.Selection),
            CoordinationOwnerStore.HashSelection(servedSelection.Selection));

        var result = await continuation.ContinueCheckpointRecoveryAsync(
            context, _actor, runtimeActor, recovered, rootIntent, registrationOwner,
            checkpointStore, fixture.Owner, NoGate, NoGate, CancellationToken.None);
        var currentRootIntent = await starts.ReadIntentForStatusAsync(
            manifest.Root.ProjectId, manifest.Root.RunId, manifest.Root.SessionId, CancellationToken.None)
            ?? throw new InvalidOperationException("The root startup intent disappeared.");
        if (recovered.CheckpointRecovery?.Phase != "pending")
        {
            Assert.Equal(recovered.CheckpointRecovery?.Phase, result.CheckpointRecovery?.Phase);
            Assert.Equal(recovered.OperationId, result.OperationId);
            Assert.Equal(recovered.ExecutionFence, result.ExecutionFence);
            Assert.Empty(peer.BootstrapSessions);
        }
        else if (emptyEvidenceReceiptName is null)
        {
            var currentRoot = ReadIntentReceipt<RuntimeRegistration>(currentRootIntent, "registration");
            var previousRoot = initial[manifest.Root.SessionId].Registration;
            Assert.True(previousRoot.Binding.ProfileId != currentRoot.Binding.ProfileId,
                $"phase={result.CheckpointRecovery?.Phase} step={result.CheckpointRecovery?.CurrentStep} " +
                $"reason={result.CheckpointRecovery?.ReasonCode}");
            Assert.NotEqual(previousRoot.Binding.ConfigureEndpoint, currentRoot.Binding.ConfigureEndpoint);
            var pendingChildren = manifest.Members.Where(member => member.Disposition == "pendingUnsent").ToArray();
            if (failRootActivation)
            {
                var pendingChild = Assert.Single(pendingChildren);
                var childIntent = await starts.ReadIntentForStatusAsync(
                    manifest.Root.ProjectId, manifest.Root.RunId,
                    pendingChild.Session.SessionId, CancellationToken.None)
                    ?? throw new InvalidOperationException("The child startup intent disappeared.");
                var currentChild = ReadIntentReceipt<RuntimeRegistration>(childIntent, "registration");
                Assert.True(
                    initial[pendingChild.Session.SessionId].Registration.Binding.ProfileId !=
                        currentChild.Binding.ProfileId,
                    $"phase={result.CheckpointRecovery?.Phase} step={result.CheckpointRecovery?.CurrentStep} " +
                    $"reason={result.CheckpointRecovery?.ReasonCode}");
                Assert.NotEqual(initial[pendingChild.Session.SessionId].Registration.Binding.ConfigureEndpoint,
                    currentChild.Binding.ConfigureEndpoint);
                Assert.Equal(manifest.Root.SessionId, Assert.Single(peer.BootstrapSessions));
                using var childLinks = JsonDocument.Parse(childIntent.ReceiptLinks);
                Assert.False(RuntimeRunStartContinuation.HasCheckpointRecoveryBootstrapEvidence(
                    result.OperationId,
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        childLinks.RootElement.GetRawText(), ChildRecoveryJsonOptions)
                    ?? new(StringComparer.Ordinal)));
            }
            else if (pendingChildren.Length == 1)
            {
                var pendingChild = pendingChildren[0];
                var childIntent = await starts.ReadIntentForStatusAsync(
                    manifest.Root.ProjectId, manifest.Root.RunId,
                    pendingChild.Session.SessionId, CancellationToken.None)
                    ?? throw new InvalidOperationException("The child startup intent disappeared.");
                var currentChild = ReadIntentReceipt<RuntimeRegistration>(childIntent, "registration");
                Assert.True(
                    initial[pendingChild.Session.SessionId].Registration.Binding.ProfileId !=
                        currentChild.Binding.ProfileId,
                    $"phase={result.CheckpointRecovery?.Phase} step={result.CheckpointRecovery?.CurrentStep} " +
                    $"reason={result.CheckpointRecovery?.ReasonCode}");
                Assert.NotEqual(initial[pendingChild.Session.SessionId].Registration.Binding.ConfigureEndpoint,
                    currentChild.Binding.ConfigureEndpoint);
                Assert.Collection(
                    peer.BootstrapSessions,
                    session => Assert.Equal(manifest.Root.SessionId, session),
                    session => Assert.Equal(pendingChild.Session.SessionId, session));
                using var childLinks = JsonDocument.Parse(childIntent.ReceiptLinks);
                Assert.True(RuntimeRunStartContinuation.HasCheckpointRecoveryBootstrapEvidence(
                    result.OperationId,
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        childLinks.RootElement.GetRawText(), ChildRecoveryJsonOptions)
                    ?? new(StringComparer.Ordinal)));
            }
            else
                Assert.Equal(manifest.Root.SessionId, Assert.Single(peer.BootstrapSessions));
        }
        return (result, currentRootIntent);
    }

    private sealed record RecoveryRuntimeSeed(
        RuntimeRegistration Registration,
        RuntimeRunStartPlacementReceipt Placement,
        Guid BootstrapOperationId);

    private sealed record RecoveryRuntimeReplacement(
        RuntimeRunStartPlacementReceipt Placement,
        string ProfileId,
        Uri ConfigureEndpoint,
        Uri ObservationEndpoint);

    private sealed class RecoveryRuntimePeer(
        IReadOnlyDictionary<string, RecoveryRuntimeSeed> initial,
        Func<string, Guid, CancellationToken, Task<RuntimeOwnerContext>> readOwner,
        string? failActivationSessionId,
        string? emptyEvidenceSessionId,
        string? emptyEvidenceReceiptName) : HttpMessageHandler
    {
        private readonly Dictionary<string, RecoveryRuntimeReplacement> _replacements =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, (RuntimeHostReadinessReceipt Activation,
            EnvironmentRuntimeReadinessContext Readiness)> _readiness = new(StringComparer.Ordinal);
        private readonly List<string> _bootstrapSessions = [];

        internal IReadOnlyList<string> BootstrapSessions => _bootstrapSessions;
        internal void RecordBootstrap(string sessionId) => _bootstrapSessions.Add(sessionId);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/internal/runtime/v1/activate")
            {
                var configure = await ReadRequestAsync<RuntimeHostConfigureRequest>(
                    request, cancellationToken).ConfigureAwait(false);
                var sessionId = configure.Registration.Binding.SessionId;
                if (sessionId == failActivationSessionId)
                    return Error(request, HttpStatusCode.Conflict, "runtime_configuration_conflict");
                var replacement = _replacements[sessionId];
                var readiness = CreateRecoveryReadiness(configure.Registration, replacement.Placement);
                _readiness[sessionId] = readiness;
                return Success(request, emptyEvidenceSessionId == sessionId &&
                    emptyEvidenceReceiptName == "activation"
                    ? readiness.Activation with { ContractVersion = 0 }
                    : readiness.Activation);
            }
            if (path.EndsWith("/readiness", StringComparison.Ordinal))
            {
                var sessionId = SessionIdFromPath(path);
                var readiness = _readiness[sessionId].Readiness;
                return Success(request, emptyEvidenceSessionId == sessionId &&
                    emptyEvidenceReceiptName == "readiness"
                    ? readiness with { ContractVersion = 0 }
                    : readiness);
            }
            if (path.EndsWith("/replacement-retire", StringComparison.Ordinal))
            {
                var envelope = await ReadRequestAsync<RuntimeReplacementRetirementRequest>(
                    request, cancellationToken).ConfigureAwait(false);
                var retirement = envelope.Retirement;
                return Success(request, new RuntimeRunStartRetirementReceipt(
                    envelope.ContractVersion,
                    retirement.BootstrapOperationId,
                    retirement.RuntimeInstanceId,
                    retirement.RegistrationRevision,
                    retirement.Placement.SandboxOperationId,
                    retirement.Placement.SandboxResource.Generation,
                    retirement.Placement.ProviderFencingGeneration,
                    retirement.Placement.SandboxIdempotencyKey,
                    "released"));
            }
            if (path.EndsWith("/replacement-placement", StringComparison.Ordinal))
            {
                var envelope = await ReadRequestAsync<RuntimeReplacementPlacementRequest>(
                    request, cancellationToken).ConfigureAwait(false);
                var previous = envelope.PreviousPlacement;
                var profileId = CreateResolvedProfileId(
                    previous.SourceProfileId, envelope.OperationId, envelope.Owner.SessionId);
                var placement = new RuntimeRunStartPlacementReceipt(
                    1,
                    previous.EnvironmentId,
                    profileId,
                    previous.WorkspaceVolumeId,
                    RuntimeRunStartKeys.SandboxIdempotencyKey(envelope.Owner, envelope.BootstrapOperationId),
                    previous.WorkspaceResourceGeneration,
                    previous.WorkspaceDataGeneration,
                    previous.NetworkPolicyGeneration,
                    Guid.NewGuid(),
                    new ProviderResourceRef(
                        ProviderSeam.Sandbox,
                        previous.SandboxResource.ProviderId,
                        $"recovered-{envelope.Owner.SessionId}",
                        previous.SandboxResource.Generation + 1),
                    previous.LifecycleGeneration,
                    previous.LeaseRevision + 1,
                    previous.CurrentFencingGeneration + 1,
                    previous.ProviderFencingGeneration + 1,
                    DateTimeOffset.UtcNow.AddHours(1))
                {
                    PodUid = $"recovered-pod-{envelope.Owner.SessionId}-{envelope.OperationId:N}"
                };
                _replacements[envelope.Owner.SessionId] = new(
                    placement, profileId,
                    new Uri($"https://runtime.test/runtime/v2/{envelope.Owner.SessionId}/{envelope.OperationId:N}/configure"),
                    new Uri($"https://runtime.test/runtime/v2/{envelope.Owner.SessionId}/{envelope.OperationId:N}/observe"));
                return Success(request, emptyEvidenceSessionId == envelope.Owner.SessionId &&
                    emptyEvidenceReceiptName == "placement"
                    ? placement with { ContractVersion = 0 }
                    : placement);
            }
            if (path.Contains("/runtime-bootstrap/profiles/", StringComparison.Ordinal) &&
                path.EndsWith("/resolve", StringComparison.Ordinal))
            {
                var sessionId = SessionIdFromPath(path);
                var segments = path.Split('/');
                var sourceProfileId = Uri.UnescapeDataString(segments[^2]);
                var resolved = _replacements.TryGetValue(sessionId, out var replacement) &&
                    replacement.Placement.SourceProfileId != sourceProfileId
                    ? CreateResolvedProfileId(
                        sourceProfileId,
                        QueryOperationId(request.RequestUri.Query),
                        sessionId)
                    : replacement?.ProfileId ?? sourceProfileId;
                return Success(request, new RuntimeBootstrapProfileResolveReceipt(
                    1, sourceProfileId, resolved));
            }
            if (path.Contains("/runtime-bootstrap/profiles/", StringComparison.Ordinal))
            {
                var sessionId = SessionIdFromPath(path);
                var profileId = Uri.UnescapeDataString(path.Split('/')[^1]);
                if (_replacements.TryGetValue(sessionId, out var replacement))
                {
                    if (profileId != replacement.ProfileId)
                        throw new InvalidOperationException("Unexpected recovery profile.");
                    return Success(request, Context(
                        initial[sessionId].Registration,
                        await readOwner(sessionId, QueryOperationId(request.RequestUri.Query), cancellationToken)
                            .ConfigureAwait(false),
                        replacement.Placement,
                        replacement.ProfileId, replacement.ConfigureEndpoint, replacement.ObservationEndpoint));
                }

                var seed = initial[sessionId];
                var owner = RuntimeRunStartContinuation.CreateOwnerContext(seed.Registration.Binding);
                return Success(request, Context(
                    seed.Registration, owner, seed.Placement, seed.Registration.Binding.ProfileId,
                    seed.Registration.Binding.ConfigureEndpoint, seed.Registration.Binding.ObservationEndpoint));
            }
            throw new InvalidOperationException($"Unexpected recovery environment route '{path}'.");
        }

        private static EnvironmentRuntimeBootstrapContext Context(
            RuntimeRegistration registration,
            RuntimeOwnerContext owner,
            RuntimeRunStartPlacementReceipt placement,
            string profileId,
            Uri configureEndpoint,
            Uri observationEndpoint) =>
            new(
                1,
                owner.TenantId,
                owner.ProjectId,
                owner.RunId,
                placement.EnvironmentId,
                placement.LifecycleGeneration,
                placement.CurrentFencingGeneration,
                placement.ProviderFencingGeneration,
                placement.LeaseRevision,
                placement.LeaseExpiresAt,
                placement.SandboxResource,
                new(Guid.NewGuid()),
                new("recovery-placement"),
                profileId,
                configureEndpoint,
                observationEndpoint)
            {
                RuntimeOwnerContext = owner,
                Image = registration.Binding.Image
            };

        private static string CreateResolvedProfileId(string sourceProfileId, Guid operationId, string sessionId)
        {
            var prefix = sourceProfileId[..Math.Min(sourceProfileId.Length, 220)];
            var suffix = RuntimeRunStartKeys.ReplacementOperationId(operationId, sessionId, "profile")
                .ToString("N")[..24];
            return $"{prefix}.r.{suffix}";
        }

        private static Guid QueryOperationId(string query)
        {
            var value = query.TrimStart('?').Split('&')
                .Single(part => part.StartsWith("checkpointRecoveryOperationId=", StringComparison.Ordinal))
                .Split('=', 2)[1];
            return Guid.Parse(Uri.UnescapeDataString(value));
        }

        private static string SessionIdFromPath(string path)
        {
            var segments = path.Split('/');
            var index = Array.IndexOf(segments, "sessions");
            return Uri.UnescapeDataString(segments[index + 1]);
        }

        private static async Task<T> ReadRequestAsync<T>(
            HttpRequestMessage request, CancellationToken cancellationToken) where T : class =>
            JsonSerializer.Deserialize<T>(
                await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
                ChildRecoveryJsonOptions)
            ?? throw new InvalidOperationException("The controlled peer request was empty.");

        internal static HttpResponseMessage Success(HttpRequestMessage request, object body) =>
            Response(request, HttpStatusCode.OK, body);

        private static HttpResponseMessage Error(HttpRequestMessage request, HttpStatusCode status, string code) =>
            Response(request, status, new { code });

        private static HttpResponseMessage Response(
            HttpRequestMessage request, HttpStatusCode status, object body)
        {
            var response = new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = JsonContent.Create(body, options: ChildRecoveryJsonOptions)
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return response;
        }
    }

    private sealed class RecoveryBrokerPeer(
        RuntimeRegistrationStore registrations,
        RecoveryRuntimePeer environmentPeer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bootstrap = JsonSerializer.Deserialize<RuntimeBootstrapRequest>(
                await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
                ChildRecoveryJsonOptions)
                ?? throw new InvalidOperationException("The controlled broker request was empty.");
            var registration = await registrations.ReadAsync(
                bootstrap.RuntimeInstanceId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The replacement registration is missing.");
            environmentPeer.RecordBootstrap(registration.Binding.SessionId);
            var receipt = new RuntimeBootstrapDeliveryReceipt(
                bootstrap.OperationId,
                Guid.NewGuid(),
                registration.RuntimeInstanceId,
                registration.Revision,
                registration.Binding.PlacementUid,
                registration.Binding.PlacementGeneration,
                registration.Binding.ExecutionFence,
                bootstrap.ConfigurationHash,
                DateTimeOffset.UtcNow)
            {
                EnvironmentCurrentFencingGeneration =
                    registration.Binding.EnvironmentCurrentFencingGeneration,
                EnvironmentProviderFencingGeneration =
                    registration.Binding.EnvironmentProviderFencingGeneration
            };
            return RecoveryRuntimePeer.Success(request, receipt);
        }
    }

    private sealed class RecoveryEventsPeer(
        SessionIdentity child,
        ChildRecoveryCompletedUnjoinedMaterial material) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Split('/');
            var sessionIndex = Array.IndexOf(path, "sessions");
            if (sessionIndex < 0 || Uri.UnescapeDataString(path[sessionIndex + 1]) != child.SessionId)
                throw new InvalidOperationException("Unexpected recovery material session.");
            var eventId = Guid.Parse(path[^2]);
            var result = eventId == material.UserMaterial.EventId
                ? new SessionMaterialReadResult(
                    material.UserMaterial, Encoding.UTF8.GetBytes(material.Prompt))
                : eventId == material.AssistantMaterial.EventId
                    ? new SessionMaterialReadResult(
                        material.AssistantMaterial, Encoding.UTF8.GetBytes(material.Result))
                    : throw new InvalidOperationException("Unexpected recovery material event.");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(result, options: ChildRecoveryJsonOptions)
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }
    }

    private sealed class RecoveryRuntimeHttpClientFactory(
        HttpClient environment,
        HttpClient broker) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            name == "runtime-run-start-broker" ? broker : environment;
    }

    private sealed class RecoveryOwnerAuthentication(DateTimeOffset expiresAt) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                context.User, new AuthenticationProperties { ExpiresUtc = expiresAt }, "recovery-test")));

        public Task ChallengeAsync(
            HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task ForbidAsync(
            HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task SignInAsync(
            HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task SignOutAsync(
            HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
    }

    private static T ReadIntentReceipt<T>(RuntimeRunStartIntent intent, string key) where T : class
    {
        using var links = JsonDocument.Parse(intent.ReceiptLinks);
        return links.RootElement.GetProperty(key).Deserialize<T>(ChildRecoveryJsonOptions)
            ?? throw new InvalidOperationException($"The startup intent has no {key} receipt.");
    }

    private async Task<CompletedUnjoinedImportContext> PrepareCompletedUnjoinedImportAsync()
    {
        var fixture = await PrepareChildRecoveryAsync(nativeState: "completed");
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var manifest = await fixture.Owner.ReadCheckpointRecoveryManifestAsync(
            _actor, _binding.Identity, recovered.OperationId, CancellationToken.None);
        var member = Assert.Single(manifest.Members);
        Assert.Equal("completedUnjoined", member.Disposition);
        Assert.Equal("partial", member.NativeOrigin!.SourceReportStatus);
        Assert.Null(member.NativeOrigin.SourceCompletionManifestHash);
        var material = CreateCompletedUnjoinedMaterial(fixture, fixture.Result);
        var checkpointStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null),
            _binding with { ExecutionFence = recovered.ExecutionFence });
        return new CompletedUnjoinedImportContext(
            fixture, recovered, manifest, checkpointStore, material);
    }

    private static ChildRecoveryCompletedUnjoinedMaterial CreateCompletedUnjoinedMaterial(
        ChildRecoveryFixture fixture,
        string result)
    {
        var registration = fixture.ChildRegistration;
        var binding = registration.Binding;
        SessionMaterialAcknowledgment Material(Guid eventId, long position, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            return new SessionMaterialAcknowledgment(
                1,
                fixture.Child,
                eventId,
                position,
                new SessionObjectReference(
                    new ObjectKey($"material/{eventId:N}"),
                    SessionMaterialValidation.Purpose(SessionMaterialKind.TurnContent),
                    bytes.Length)
                {
                    Material = new SessionMaterialBinding(
                        1,
                        SessionMaterialKind.TurnContent,
                        binding.TenantId,
                        RuntimeContractValidation.Hash(bytes),
                        registration.RuntimeInstanceId,
                        registration.Revision,
                        binding.ExecutionFence,
                        binding.AcceptedSelectionHash,
                        "1.0.18",
                        "1.0.97",
                        binding.ModelSelectionReference!,
                        "gpt-6.1-sol")
                });
        }

        return new(
            fixture.Prompt,
            result,
            Material(fixture.Dispatch.MessageId, 1, fixture.Prompt),
            Material(
                RuntimeRootTurnCompletionReceipt.AssistantEventId(fixture.Dispatch.MessageId),
                2,
                result));
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
        var fixtureId = Guid.NewGuid().ToString("N");
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
        var rootEnvironmentId = RuntimeRunStartKeys.EnvironmentId(rootRuntime);
        var registrationStore = new RuntimeRegistrationStore(_fixture.DataSource, _schema, TimeProvider.System);
        var rootRegistration = await registrationStore.RegisterAsync(new RuntimeBinding(
            _actor.Issuer, _actor.Subject, "tenant-1", _binding.Identity.ProjectId, _binding.Identity.RunId,
            _binding.Identity.SessionId, CoordinationEndpoints.CoordinatorRuntimeAgentId, "root-turn", 1, 1, 1,
            _selection.Selection.ContextRevision, selectionHash.ToLowerInvariant(), _binding.ExecutionFence,
            rootEnvironmentId, $"root-resource-{fixtureId}", 1, "profile-1",
            new($"https://runtime-{fixtureId}.test/runtime/v1/configure"),
            new($"https://runtime-{fixtureId}.test/observation"))
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
        var rootBootstrapId = rootIntent.BootstrapOperationId;
        var rootPlacement = new RuntimeRunStartPlacementReceipt(
            1, rootEnvironmentId, "profile-1", RuntimeRunStartKeys.WorkspaceVolumeId(rootRuntime),
            RuntimeRunStartKeys.SandboxIdempotencyKey(rootRuntime, rootBootstrapId),
            1, 0, 1, Guid.NewGuid(),
            new ProviderResourceRef(ProviderSeam.Sandbox, ReviewSandboxProviderId, $"root-resource-{fixtureId}", 1),
            1, 1, 1, 1, rootRegistration.ExpiresAt) { PodUid = $"root-pod-{fixtureId}" };
        var rootBootstrap = new RuntimeBootstrapDeliveryReceipt(
            rootBootstrapId, Guid.NewGuid(), rootRuntimeId, rootRegistration.Revision,
            rootRegistration.Binding.PlacementUid, rootRegistration.Binding.PlacementGeneration,
            rootRegistration.Binding.ExecutionFence, new string('d', 64), DateTimeOffset.UtcNow)
        {
            EnvironmentCurrentFencingGeneration = 1,
            EnvironmentProviderFencingGeneration = 1
        };
        _ = await starts.UpdateAsync(rootIntent, rootIntent.RowVersion, "ready", "ready", null,
            rootEnvironmentId, rootRuntimeId,
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
                        _binding.Identity.RunId, rootEnvironmentId), 1),
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
        var childEnvironmentId = RuntimeRunStartKeys.EnvironmentId(
            "tenant-1", _binding.Identity.ProjectId, _binding.Identity.RunId, childId,
            selectionHash.ToLowerInvariant());
        var childIntent = await starts.ReadIntentForStatusAsync(
            _binding.Identity.ProjectId, _binding.Identity.RunId, childId, CancellationToken.None);
        Assert.NotNull(childIntent);
        var registration = await registrationStore.RegisterAsync(new RuntimeBinding(
                _actor.Issuer, _actor.Subject, "tenant-1", _binding.Identity.ProjectId, _binding.Identity.RunId,
                childId, item.AgentId, "child-turn", 1, 1, 1, _selection.Selection.ContextRevision,
                selectionHash.ToLowerInvariant(), _binding.ExecutionFence, childEnvironmentId,
                $"child-resource-{fixtureId}", 1, "profile-1",
                new($"https://runtime-{fixtureId}.test/runtime/v1/configure"),
                new($"https://runtime-{fixtureId}.test/observation"))
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
        var childRuntime = RuntimeRunStartContinuation.CreateOwnerContext(registration.Binding);
        var childBootstrapId = childIntent.BootstrapOperationId;
        var placement = new RuntimeRunStartPlacementReceipt(
            1, childEnvironmentId, "profile-1", RuntimeRunStartKeys.WorkspaceVolumeId(childRuntime),
            RuntimeRunStartKeys.SandboxIdempotencyKey(childRuntime, childBootstrapId),
            1, 0, 1, Guid.NewGuid(),
            new ProviderResourceRef(ProviderSeam.Sandbox, ReviewSandboxProviderId, $"child-resource-{fixtureId}", 1),
            1, 1, 1, 1, registration.ExpiresAt) { PodUid = $"child-pod-{fixtureId}" };
        var bootstrap = new RuntimeBootstrapDeliveryReceipt(
            childBootstrapId, Guid.NewGuid(), registration.RuntimeInstanceId,
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
        else if (nativeState == "completed")
            await PrepareChildNativeAsync(
                registration, dispatch, prompt, source, begin: true, complete: true,
                result: CompletedUnjoinedResult);
        var run = await owner.ReadOwnerRunStatusAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, CancellationToken.None);
        var request = new RecoverRunExecutionRequest(
            _binding.ExecutionFence, run.StateVersion, "child-recovery", "operator_recovery", "lost-host")
        {
            CheckpointRecovery = new CheckpointRecoveryRequest(
                source.Info.CheckpointId, source.State.Revision, decision.StateVersion,
                rootRuntimeId, 1, selectionHash.ToLowerInvariant())
        };
        return new ChildRecoveryFixture(
            owner, decisions, spawned.Node.Identity, childIntent, plan.Plan, source, request,
            registration, dispatch, prompt, CompletedUnjoinedResult);
    }

    private async Task PrepareChildNativeAsync(
        RuntimeRegistration registration,
        MafExecutionDispatchIntent dispatch,
        string prompt,
        MafExecutionCheckpointSnapshot checkpoint,
        bool begin,
        bool complete = false,
        string? result = null)
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
        var beginResult = await store.ExecuteLockedAsync(registration.RuntimeInstanceId, async (connection, transaction, token) =>
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
        if (!complete)
            return;

        var admission = beginResult.Admission
            ?? throw new InvalidOperationException("The native turn did not begin.");
        var answer = result ?? throw new InvalidOperationException("A completed result is required.");
        var observation = new RuntimeNativeTurnObservation(
            source,
            Guid.NewGuid().ToString("D"),
            "controlled-native-turn",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddSeconds(-1),
            "controlled-cursor",
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(answer)),
            null)
        {
            UsageEventIds = []
        };
        var recorded = await store.ExecuteLockedAsync(
            registration.RuntimeInstanceId, async (connection, transaction, token) =>
            {
                var receipt = await store.RecordNativeTurnWithinTransactionAsync(
                    connection, transaction, registration, grant,
                    new(null!, admission, observation), token);
                await transaction.CommitAsync(token);
                return receipt;
            }, CancellationToken.None);
        var accounting = new RuntimeNativeTurnAccountingRequest(null!, recorded, []);
        var snapshot = CreateZeroUsageEstimateSnapshot(registration, source, dispatch.MessageId);
        _ = await store.ExecuteLockedAsync(
            registration.RuntimeInstanceId, async (connection, transaction, token) =>
            {
                var receipt = await store.CompleteNativeTurnWithinTransactionAsync(
                    connection, transaction, registration, grant, accounting, snapshot, token);
                await transaction.CommitAsync(token);
                return receipt;
            }, CancellationToken.None);
    }

    private static RuntimeUsageCostSnapshotReceipt CreateZeroUsageEstimateSnapshot(
        RuntimeRegistration registration,
        SdkSessionFacts source,
        Guid dispatchId)
    {
        var card = new CostRateCard(
            "child-recovery-card", "2026-10-17", SdkMeterSources.CopilotNanoAiu, "AIC",
            1_000_000_000m, ImmutableDictionary<string, decimal>.Empty)
        {
            ProviderSourceUrl =
                "https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing",
            SnapshotVersion = "2026-10-17",
            RetrievedOn = new DateOnly(2026, 10, 17),
            TokenRateCurrency = "USD",
            TokensPerRateUnit = 1_000_000m,
            CreditsPerCurrencyUnit = 100m,
            LongContextThresholdTokens = 272_000,
            ContentHash = new string('a', 64),
            ModelTokenRates = ImmutableDictionary<string, CostModelTokenRates>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(source.ModelId, new(
                    new(2m, 0.1m, 2.5m, 10m),
                    new(4m, 0.2m, 5m, 15m))
                )
        };
        var binding = new CostBinding(
            SdkMeterSources.CopilotNanoAiu, "copilot.usage-cost", "1.0.0", 1,
            "cost-options", "cost", 1, ImmutableHashSet<string>.Empty, card);
        var scope = registration.Binding;
        return new RuntimeUsageCostSnapshotReceipt(
            1,
            RuntimeUsageSourceReceiptContract.HashSource(registration, source),
            binding,
            new(0, "AIC", CostDisposition.Estimate, card, null),
            new(scope.TenantId, scope.ProjectId, scope.RunId, 0, true, [], []))
        {
            DispatchId = dispatchId,
            RepresentedReceipts = [],
            RateCardContentHash = card.ContentHash
        };
    }

    private sealed record ChildRecoveryFixture(
        CoordinationOwnerStore Owner,
        CoordinatorDecisionOwnerStore Decisions,
        SessionIdentity Child,
        RuntimeRunStartIntent ChildIntent,
        WorkPlan Plan,
        MafExecutionCheckpointSnapshot Source,
        RecoverRunExecutionRequest Request,
        RuntimeRegistration ChildRegistration,
        MafExecutionDispatchIntent Dispatch,
        string Prompt,
        string Result);

    private sealed record ReadyNativeRebindContext(
        ChildRecoveryFixture Fixture,
        RunExecutionTransitionResult Ready,
        ChildCheckpointRecoveryManifest Manifest,
        MafExecutionCheckpointStore CheckpointStore,
        MafExecutionCheckpointSnapshot Checkpoint,
        CoordinationOwnerStore RestartOwner);

    private sealed record NativeDispatchState(string State, long OwnerRevision);

    private sealed record CompletedUnjoinedImportContext(
        ChildRecoveryFixture Fixture,
        RunExecutionTransitionResult Recovered,
        ChildCheckpointRecoveryManifest Manifest,
        MafExecutionCheckpointStore CheckpointStore,
        ChildRecoveryCompletedUnjoinedMaterial Material);
}
