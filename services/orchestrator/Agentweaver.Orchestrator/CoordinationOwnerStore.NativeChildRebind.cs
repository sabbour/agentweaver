using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed partial class CoordinationOwnerStore
{
    private async Task LockCheckpointRecoverySessionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ChildCheckpointRecoveryManifest manifest,
        CancellationToken cancellationToken)
    {
        var required = manifest.Members
            .Where(member => member.Disposition is "pendingUnsent" or "completedUnjoined")
            .OrderBy(member => member.Session.SessionId, StringComparer.Ordinal)
            .ToArray();
        if (required.Length == 0)
            return;

        await using var command = new NpgsqlCommand($"""
            SELECT session_id, parent_session_id, root_session_id, detached, execution_fence,
                   lifecycle_state, turn_state, logical_turn_ordinal, state_version,
                   work_plan_item_id, archived_at
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run
              AND session_id = ANY(@sessions)
            ORDER BY session_id
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, manifest.Root.ProjectId, manifest.Root.RunId);
        command.Parameters.AddWithValue(
            "sessions", NpgsqlDbType.Array | NpgsqlDbType.Varchar,
            required.Select(member => member.Session.SessionId).ToArray());
        var index = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (index >= required.Length)
                throw new CoordinationException(
                    "checkpoint_recovery_members_not_ready", StatusCodes.Status409Conflict);
            var member = required[index++];
            if (reader.GetString(0) != member.Session.SessionId ||
                reader.GetString(1) != manifest.Root.SessionId ||
                reader.GetString(2) != manifest.Root.SessionId ||
                reader.GetBoolean(3) ||
                reader.GetInt64(4) != manifest.HoldFence ||
                reader.GetString(5) != "active" ||
                reader.GetString(6) != "active" ||
                reader.GetInt64(7) != member.Session.LogicalTurnOrdinal ||
                reader.GetInt64(8) != checked(member.Session.StateVersion + 1) ||
                (reader.IsDBNull(9) ? null : reader.GetString(9)) != member.Session.WorkPlanItemId ||
                !reader.IsDBNull(10))
                throw new CoordinationException(
                    "checkpoint_recovery_members_not_ready", StatusCodes.Status409Conflict);
        }
        if (index != required.Length)
            throw new CoordinationException(
                "checkpoint_recovery_members_not_ready", StatusCodes.Status409Conflict);
    }

    private async Task AppendNativeChildRebindsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        AcceptedRunRow run,
        CoordinationActor actor,
        Guid operationId,
        CheckpointRecoveryStatus status,
        ChildCheckpointRecoveryManifest manifest,
        MafExecutionCheckpointSnapshot anchor,
        RuntimeRunStartIntent rootIntent,
        WorkPlanSnapshot plan,
        RuntimeUsageSourceStore usageSources,
        CancellationToken cancellationToken)
    {
        var lineage = anchor.State.RecoveryLineage;
        if (status.Phase != "ready" || status.DispatchHeld != false ||
            status.BoundaryKind != "childJoin" ||
            anchor.Info.CheckpointId != status.RecoveryCheckpointId ||
            anchor.State.Revision != status.RecoveryCheckpointRevision ||
            anchor.State.DecisionStateVersion != status.DecisionStateVersion ||
            anchor.State.WorkPlanId != manifest.WorkPlanId ||
            lineage is null || lineage.RecoveryOperationId != operationId ||
            lineage.RecoveryExecutionFence != status.ExecutionFence ||
            lineage.SourceCheckpointId != manifest.SourceCheckpointId ||
            lineage.SourceCheckpointRevision != manifest.SourceCheckpointRevision ||
            lineage.SourceCheckpointHash != manifest.SourceCheckpointHash ||
            manifest.Root != identity || manifest.TenantId != run.TenantId ||
            manifest.ActorIssuer != actor.Issuer || manifest.ActorId != actor.Subject ||
            !string.Equals(manifest.SelectionHash, run.SelectionHash, StringComparison.OrdinalIgnoreCase) ||
            manifest.HoldFence != status.ExecutionFence ||
            manifest.SourceExecutionFence != status.PreviousExecutionFence ||
            rootIntent.ExecutionFence != status.ExecutionFence)
            throw new CoordinationException(
                "checkpoint_recovery_native_rebind_invalid", StatusCodes.Status409Conflict);

        var ledger = new RuntimeExecutionEvidenceLedgerStore(_schema[1..^1]);
        _ = await ledger.RequireNativeChildRebindSchemaAsync(
            connection, transaction, requireApplied: true, cancellationToken).ConfigureAwait(false);
        var members = manifest.Members
            .Where(member => member.Disposition == "pendingUnsent" && member.NativeOrigin is not null)
            .OrderBy(member => member.Session.SessionId, StringComparer.Ordinal)
            .ToArray();
        foreach (var member in members)
        {
            if (member.NativeOrigin is not { } native ||
                native.State != "prepared" || native.OwnerRevision != 1 ||
                native.TerminalOutcome is not null || native.SourceReportStatus != "unknown" ||
                native.SourceReportHash is not null || native.SourceCompletionManifestHash is not null ||
                native.AcknowledgmentReferencesHash !=
                    ChildCheckpointRecoveryManifestCodec.Hash(Array.Empty<JsonElement>()) ||
                native.AccountingStatus != "unknown" || native.UsageWitnessWatermark is not null ||
                native.UsageWitnessDigest is not null || native.RetiredThroughEventPosition is not null)
                throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_invalid", StatusCodes.Status409Conflict);

            var lockedOrigin = await ChildCheckpointRecoveryCensus.ReadNativeOriginAsync(
                connection, transaction, _schema, identity, member.Session, member.Startup.Binding,
                manifest.SelectionHash, cancellationToken).ConfigureAwait(false);
            if (lockedOrigin != native)
                throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_conflict", StatusCodes.Status409Conflict);

            var originalRegistration = await new RuntimeRegistrationStore(
                _dataSource, _schema[1..^1], _timeProvider).ReadRevisionAsync(
                    connection, transaction, native.RuntimeInstanceId, native.RegistrationRevision,
                    cancellationToken).ConfigureAwait(false)
                ?? throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_conflict", StatusCodes.Status409Conflict);
            var originalSource = await usageSources.ReadSourceAsync(
                connection, transaction, native.RuntimeInstanceId, cancellationToken).ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
            if (originalSource.Receipt.CanonicalPayloadHash != native.SdkSourceReceiptHash)
                throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_conflict", StatusCodes.Status409Conflict);

            var rebinds = await ledger.ReadNativeChildRebindsAsync(
                connection, transaction, identity.ProjectId, identity.RunId, native.DispatchId,
                cancellationToken).ConfigureAwait(false);
            var prior = rebinds.LastOrDefault();
            if ((prior is null) != (member.PriorNativeRebindReceiptId is null) ||
                prior is not null &&
                (prior.ReceiptId != member.PriorNativeRebindReceiptId ||
                 prior.CanonicalPayloadHash != member.PriorNativeRebindPayloadHash))
                throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_conflict", StatusCodes.Status409Conflict);
            if (prior is not null)
            {
                var priorRegistration = await new RuntimeRegistrationStore(
                    _dataSource, _schema[1..^1], _timeProvider).ReadRevisionAsync(
                        connection, transaction, prior.RuntimeInstanceId, prior.RegistrationRevision,
                        cancellationToken).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "checkpoint_recovery_native_rebind_conflict", StatusCodes.Status409Conflict);
                var validatedPrior = await usageSources.ValidateNativeChildRebindChainAsync(
                    connection, transaction, priorRegistration, lockedOrigin, native.DispatchId,
                    requireReleasedHold: false, cancellationToken).ConfigureAwait(false);
                if (validatedPrior is null ||
                    validatedPrior.ReceiptId != prior.ReceiptId ||
                    validatedPrior.CanonicalPayloadHash != prior.CanonicalPayloadHash)
                    throw new CoordinationException(
                        "checkpoint_recovery_native_rebind_conflict", StatusCodes.Status409Conflict);
            }

            var (registration, readiness) = await ReadReadyChildBindingAsync(
                connection, transaction, manifest, member, cancellationToken).ConfigureAwait(false);
            var source = await usageSources.ReadSourceAsync(
                connection, transaction, registration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
            if (source.Receipt.RegistrationRevision != registration.Revision ||
                source.Receipt.SourceGrantId != readiness.SourceGrant.GrantId ||
                source.ConfigurationHash != readiness.SourceGrant.ConfigurationHash ||
                source.Receipt.CanonicalPayloadHash !=
                    RuntimeUsageSourceReceiptContract.HashSource(registration, source.Receipt.Source) ||
                source.Receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
                registration.Binding.PlacementProviderId != member.IsolationProviderId ||
                originalRegistration.Binding.PlacementProviderId != member.IsolationProviderId ||
                !NativeChildRebindOriginFingerprint.HasSameModelAndCostBinding(
                    originalRegistration, registration, native,
                    originalSource.Receipt.Source, source.Receipt.Source))
                throw new RuntimeAuthorizationException("runtime_sdk_source_conflict");

            var prompt = CoordinationEndpoints.BuildNativeChildPrompt(
                plan, anchor.State, member.AssociationId, member.Session.SessionId);
            var requestHash = native.RequestHash;
            if (RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt)) != member.PromptHash)
                throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_conflict", StatusCodes.Status409Conflict);
            var effectiveRequest = MafExecutionDispatchRequestBuilder.Build(
                registration, readiness, member.MessageId, prompt, manifest.WorkPlanId,
                member.AssociationId, member.WorkItemBindingSha256);
            var effectiveRequestHash = RuntimeNativeTurnContract.RequestHash(effectiveRequest);
            var origin = NativeChildRebindOriginFingerprint.Create(manifest, member);
            if (origin.RequestHash != native.RequestHash ||
                member.MessageRequestHash is not null &&
                member.MessageRequestHash != requestHash ||
                member.EgressReceiptHash is null)
                throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_conflict", StatusCodes.Status409Conflict);

            CheckpointRecoverySupersessionReceipt? supersession = null;
            if (lineage.SupersedesOperationId is { } supersededOperationId)
            {
                supersession = await ledger.ReadCheckpointRecoverySupersessionAsync(
                    connection, transaction, identity.ProjectId, identity.RunId, supersededOperationId,
                    cancellationToken).ConfigureAwait(false);
                if (supersession is null ||
                    supersession.ReceiptId != lineage.SupersessionReceiptId ||
                    supersession.CanonicalPayloadHash != lineage.SupersessionPayloadHash ||
                    supersession.SuccessorOperationId != operationId)
                    throw new CoordinationException(
                        "checkpoint_recovery_native_rebind_invalid", StatusCodes.Status409Conflict);
            }
            else if (lineage.SupersessionReceiptId is not null || lineage.SupersessionPayloadHash is not null)
                throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_invalid", StatusCodes.Status409Conflict);

            var edge = new NativeChildRebindCheckpointReference(
                operationId,
                manifest.SourceCheckpointId,
                manifest.SourceCheckpointRevision,
                manifest.SourceCheckpointHash,
                manifest.SourceExecutionFence,
                anchor.Info.CheckpointId,
                anchor.State.Revision,
                anchor.PayloadHash,
                manifest.HoldFence,
                lineage.SupersedesOperationId,
                lineage.SupersessionReceiptId,
                lineage.SupersessionPayloadHash);
            var receipt = NativeChildRebindReceipt.Create(
                manifest.TenantId,
                actor,
                manifest.SelectionHash,
                manifest,
                member,
                origin,
                anchor,
                supersession?.ReceiptId,
                supersession?.CanonicalPayloadHash,
                registration,
                source.Receipt.CanonicalPayloadHash,
                readiness.SourceGrant.GrantId,
                readiness.SourceGrant.Revision,
                effectiveRequestHash,
                requestHash,
                member.EgressReceiptHash,
                [edge]);
            _ = await ledger.AppendNativeChildRebindAsync(
                connection, transaction, receipt, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task ValidateNativeChildRebindsBeforeReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ChildCheckpointRecoveryManifest manifest,
        RuntimeUsageSourceStore usageSources,
        CancellationToken cancellationToken)
    {
        var ledger = new RuntimeExecutionEvidenceLedgerStore(_schema[1..^1]);
        var registrations = new RuntimeRegistrationStore(_dataSource, _schema[1..^1], _timeProvider);
        foreach (var member in manifest.Members.Where(member => member.NativeOrigin is not null))
        {
            var origin = member.NativeOrigin!;
            var requiresNewRebind = member.Disposition == "pendingUnsent" &&
                origin.State == "prepared";
            var receipts = await ledger.ReadNativeChildRebindsAsync(
                connection, transaction, manifest.Root.ProjectId, manifest.Root.RunId,
                origin.DispatchId, cancellationToken).ConfigureAwait(false);
            if (receipts.IsEmpty)
            {
                if (member.PriorNativeRebindReceiptId is not null ||
                    member.PriorNativeRebindPayloadHash is not null ||
                    requiresNewRebind)
                    throw new CoordinationException(
                        "checkpoint_recovery_native_rebind_invalid", StatusCodes.Status409Conflict);
                continue;
            }

            var latest = receipts[^1];
            var isNewRebind = member.Disposition == "pendingUnsent" &&
                origin.State == "prepared" &&
                latest.RecoveryOperationId == manifest.OperationId &&
                latest.DestinationFence == manifest.HoldFence;
            if (requiresNewRebind && !isNewRebind)
                throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_invalid", StatusCodes.Status409Conflict);
            if (isNewRebind)
            {
                if (latest.OriginFingerprint !=
                    NativeChildRebindOriginFingerprint.Create(manifest, member).Hash)
                    throw new CoordinationException(
                        "checkpoint_recovery_native_rebind_invalid", StatusCodes.Status409Conflict);
            }
            else if (latest.ReceiptId != member.PriorNativeRebindReceiptId ||
                     latest.CanonicalPayloadHash != member.PriorNativeRebindPayloadHash)
                throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_invalid", StatusCodes.Status409Conflict);

            var lockedOrigin = await ChildCheckpointRecoveryCensus.ReadNativeOriginAsync(
                connection, transaction, _schema, manifest.Root, member.Session, member.Startup.Binding,
                manifest.SelectionHash, cancellationToken).ConfigureAwait(false);
            if (lockedOrigin != origin)
                throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_conflict", StatusCodes.Status409Conflict);

            var registration = await registrations.ReadRevisionAsync(
                connection, transaction, latest.RuntimeInstanceId, latest.RegistrationRevision,
                cancellationToken).ConfigureAwait(false)
                ?? throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_invalid", StatusCodes.Status409Conflict);
            var validated = await usageSources.ValidateNativeChildRebindChainAsync(
                connection, transaction, registration, lockedOrigin, origin.DispatchId,
                requireReleasedHold: false, cancellationToken).ConfigureAwait(false);
            if (validated is null ||
                validated.ReceiptId != latest.ReceiptId ||
                validated.CanonicalPayloadHash != latest.CanonicalPayloadHash)
                throw new CoordinationException(
                    "checkpoint_recovery_native_rebind_invalid", StatusCodes.Status409Conflict);
        }
    }

    private async Task<(RuntimeRegistration Registration, RuntimeHostReadinessReceipt Readiness)>
        ReadReadyChildBindingAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            ChildCheckpointRecoveryManifest manifest,
            ChildRecoveryMember member,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_links::text
            FROM {_schema}.runtime_run_start_intents
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @session AND accepted_selection_hash = @selection
              AND execution_fence = @fence AND state = 'ready' AND current_step = 'ready'
              AND NOT cleanup_obligation
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, manifest.TenantId);
        AddRunScope(command, manifest.Root.ProjectId, manifest.Root.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, member.Session.SessionId);
        command.Parameters.AddWithValue("selection", NpgsqlDbType.Char, manifest.SelectionHash.ToLowerInvariant());
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, manifest.HoldFence);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "checkpoint_recovery_members_not_ready", StatusCodes.Status409Conflict);
        using var links = JsonDocument.Parse(json);
        var registration = links.RootElement.GetProperty("registration")
            .Deserialize<RuntimeRegistration>(JsonOptions)
            ?? throw new CoordinationException(
                "checkpoint_recovery_members_not_ready", StatusCodes.Status409Conflict);
        var readiness = links.RootElement.GetProperty("activation")
            .Deserialize<RuntimeHostReadinessReceipt>(JsonOptions)
            ?? throw new CoordinationException(
                "checkpoint_recovery_members_not_ready", StatusCodes.Status409Conflict);
        RuntimeContractValidation.Validate(registration);
        CoordinationEndpoints.ValidateHostReadiness(readiness, registration, _timeProvider);
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= _timeProvider.GetUtcNow() ||
            registration.Binding.TenantId != manifest.TenantId ||
            registration.Binding.ProjectId != manifest.Root.ProjectId ||
            registration.Binding.RunId != manifest.Root.RunId ||
            registration.Binding.SessionId != member.Session.SessionId ||
            registration.Binding.ExecutionFence != manifest.HoldFence ||
            !string.Equals(
                registration.Binding.AcceptedSelectionHash, manifest.SelectionHash,
                StringComparison.OrdinalIgnoreCase) ||
            registration.Binding.ModelSelectionReference != member.ModelSelectionReference ||
            readiness.RuntimeInstanceId != registration.RuntimeInstanceId ||
            readiness.RegistrationRevision != registration.Revision ||
            readiness.ExecutionFence != manifest.HoldFence)
            throw new CoordinationException(
                "checkpoint_recovery_members_not_ready", StatusCodes.Status409Conflict);
        return (registration, readiness);
    }

}
