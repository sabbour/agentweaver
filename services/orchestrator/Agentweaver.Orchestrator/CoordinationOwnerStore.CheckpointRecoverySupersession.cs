using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed partial class CoordinationOwnerStore
{
    private async Task<CheckpointRecoverySupersessionSource> ReadCheckpointRecoverySupersessionSourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        string tenantId,
        SessionIdentity identity,
        long currentFence,
        string selectionHash,
        MafExecutionCheckpointSnapshot sourceCheckpoint,
        ChildCheckpointRecoveryManifest successorManifest,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT operation_id, request_hash, result::text, progress::text, phase_version
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND operation_kind = 'recovery'
              AND result -> 'checkpointRecovery' ->> 'boundaryKind' = 'childJoin'
              AND (result ->> 'executionFence')::bigint = @fence
            ORDER BY created_at DESC
            LIMIT 2
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, currentFence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);
        var operationId = reader.GetGuid(0);
        var requestHash = reader.GetString(1).Trim();
        var resultJson = reader.GetString(2);
        var progressJson = reader.GetString(3);
        var phaseVersion = reader.GetInt64(4);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);
        await reader.DisposeAsync().ConfigureAwait(false);

        var result = JsonSerializer.Deserialize<RunExecutionTransitionResult>(resultJson, JsonOptions);
        using var progress = JsonDocument.Parse(progressJson);
        if (result is null || result.CheckpointRecovery is not { BoundaryKind: "childJoin" } status ||
            result.OperationId != operationId || result.Session != identity ||
            result.ExecutionFence != currentFence)
            throw new CoordinationException(
                "checkpoint_recovery_supersession_unavailable",
                StatusCodes.Status409Conflict);
        if (status.Phase == "pending")
            throw new CoordinationException(
                "checkpoint_recovery_dispatch_held", StatusCodes.Status409Conflict);
        if (phaseVersion < 1 || status.Phase != "blocked" ||
            status.CurrentStep != "activation" ||
            status.ReasonCode is not ("checkpoint_recovery_host_bootstrap_spent" or
                "runtime_activation_owner_unavailable" or "runtime_activation_owner_timeout" or
                "runtime_owner_unavailable" or "runtime_owner_timeout") ||
            status.DispatchHeld != true ||
            status.RecoveryCheckpointId != sourceCheckpoint.Info.CheckpointId ||
            status.RecoveryCheckpointRevision != sourceCheckpoint.State.Revision ||
            status.RecoveryCheckpointHash != sourceCheckpoint.PayloadHash ||
            status.ExecutionFence != currentFence ||
            status.PreviousExecutionFence + 1 != currentFence ||
            status.ChildManifestSha256 is null ||
            sourceCheckpoint.State.RecoveryLineage is not { } lineage ||
            lineage.RecoveryOperationId != operationId ||
            lineage.RecoveryExecutionFence != currentFence ||
            lineage.SourceCheckpointId != status.SourceCheckpointId ||
            lineage.SourceCheckpointRevision != status.SourceCheckpointRevision ||
            lineage.SourceCheckpointHash != status.SourceCheckpointHash)
            throw new CoordinationException(
                "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);

        var supersededManifest = ReadCheckpointRecoveryManifest(result, progress.RootElement);
        if (supersededManifest.TenantId != tenantId ||
            supersededManifest.Root != identity ||
            supersededManifest.ActorIssuer != actor.Issuer ||
            supersededManifest.ActorId != actor.Subject ||
            !string.Equals(supersededManifest.SelectionHash, selectionHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(supersededManifest.WorkPlanId, sourceCheckpoint.State.WorkPlanId, StringComparison.Ordinal) ||
            supersededManifest.HoldFence != currentFence ||
            successorManifest.Root != identity ||
            successorManifest.TenantId != tenantId ||
            successorManifest.ActorIssuer != actor.Issuer ||
            successorManifest.ActorId != actor.Subject ||
            !string.Equals(successorManifest.SelectionHash, selectionHash, StringComparison.OrdinalIgnoreCase) ||
            successorManifest.SourceCheckpointId != sourceCheckpoint.Info.CheckpointId ||
            successorManifest.SourceCheckpointRevision != sourceCheckpoint.State.Revision ||
            successorManifest.SourceCheckpointHash != sourceCheckpoint.PayloadHash ||
            successorManifest.SourceExecutionFence != currentFence ||
            successorManifest.HoldFence != checked(currentFence + 1) ||
            !string.Equals(supersededManifest.WorkPlanId, successorManifest.WorkPlanId, StringComparison.Ordinal) ||
            !string.Equals(supersededManifest.WorkPlanHash, successorManifest.WorkPlanHash, StringComparison.Ordinal) ||
            !string.Equals(supersededManifest.WorkflowHash, successorManifest.WorkflowHash, StringComparison.Ordinal) ||
            CheckpointRecoverySupersessionSource.ChildIdentityHash(supersededManifest) !=
                CheckpointRecoverySupersessionSource.ChildIdentityHash(successorManifest) ||
            supersededManifest.OtherNativeWork.Length != 0 ||
            successorManifest.OtherNativeWork.Length != 0)
            throw new CoordinationException(
                "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);

        var pendingUnsent = supersededManifest.Members
            .Where(member => member.Disposition == "pendingUnsent")
            .OrderBy(member => member.AssociationId, StringComparer.Ordinal)
            .ToArray();
        if (pendingUnsent.Length == 0 ||
            pendingUnsent.Any(member => !member.NativeOriginProvenAbsent || member.NativeOrigin is not null) ||
            CheckpointRecoveryMemberBarrier.RequiredMembers(successorManifest).Length != pendingUnsent.Length)
            throw new CoordinationException(
                "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);

        var noSendHash = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            pendingUnsent.Select(member => new
            {
                member.AssociationId,
                member.Session.SessionId,
                member.MessageId,
                member.PromptHash,
                member.NativeOriginProvenAbsent,
                member.Startup.Binding,
                member.Startup.RegistrationId,
                member.Startup.RegistrationRevision
            }), JsonOptions));
        var registrationSet = await ReadCheckpointRecoveryReplacementSetAsync(
            connection, transaction, tenantId, identity, selectionHash, currentFence,
            operationId, supersededManifest, pendingUnsent, cancellationToken).ConfigureAwait(false);
        var admissionHash = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            operationId,
            result.PreviousExecutionFence,
            result.ExecutionFence,
            result.PreviousRunStateVersion,
            result.RunStateVersion,
            status.SourceCheckpointId,
            status.SourceCheckpointRevision,
            status.SourceCheckpointHash,
            status.RecoveryCheckpointId,
            status.RecoveryCheckpointRevision,
            status.RecoveryCheckpointHash,
            status.ChildManifestSha256,
            status.BoundaryKind,
            status.GateRequestId,
            status.GateAuthorizedActorId
        }, JsonOptions));

        var ledger = new RuntimeExecutionEvidenceLedgerStore(_schema[1..^1]);
        if (await ledger.ReadCheckpointRecoverySupersessionAsync(
                connection, transaction, identity.ProjectId, identity.RunId, operationId, cancellationToken)
            .ConfigureAwait(false) is not null)
            throw new CoordinationException(
                "checkpoint_recovery_supersession_conflict", StatusCodes.Status409Conflict);

        return new CheckpointRecoverySupersessionSource(
            operationId,
            requestHash,
            status.ChildManifestSha256,
            admissionHash,
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(resultJson)),
            phaseVersion,
            status,
            registrationSet.Count,
            registrationSet.Hash,
            pendingUnsent.Length,
            noSendHash);
    }

    private async Task<(int Count, string Hash)> ReadCheckpointRecoveryReplacementSetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        SessionIdentity identity,
        string selectionHash,
        long currentFence,
        Guid operationId,
        ChildCheckpointRecoveryManifest manifest,
        IReadOnlyList<ChildRecoveryMember> members,
        CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string,
            (Guid IntentId, Guid PreviousRuntimeId, long PreviousRevision, Guid PreviousBootstrapOperationId,
                RuntimeRunStartPlacementReceipt PreviousPlacement, string PreviousBindingHash)>(
            StringComparer.Ordinal)
        {
            [identity.SessionId] = (
                manifest.RootStartup.IntentId,
                manifest.RootStartup.RuntimeInstanceId,
                manifest.RootStartup.RegistrationRevision,
                manifest.RootStartup.BootstrapOperationId,
                manifest.RootStartup.Placement,
                manifest.RootStartup.RegistrationBindingHash)
        };
        foreach (var member in members)
        {
            if (member.Startup.RegistrationId is not { } runtimeId ||
                member.Startup.RegistrationRevision is not { } revision ||
                member.Startup.Placement is not { } previousPlacement ||
                member.Startup.RegistrationBindingHash is not { } bindingHash ||
                !expected.TryAdd(member.Session.SessionId,
                    (member.Startup.IntentId, runtimeId, revision, member.Startup.BootstrapOperationId,
                        previousPlacement, bindingHash)))
                throw new CoordinationException(
                    "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);
        }

        var sessions = expected.Keys.Order(StringComparer.Ordinal).ToArray();
        var entries = new List<object>(sessions.Length);
        await using var command = new NpgsqlCommand($"""
            SELECT root_session_id, accepted_selection_hash, execution_fence, intent_id, state,
                   environment_id, runtime_instance_id, receipt_links::text, cleanup_obligation, row_version
            FROM {_schema}.runtime_run_start_intents
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = ANY(@sessions) AND lower(accepted_selection_hash) = @selection
            ORDER BY root_session_id
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
        command.Parameters.AddWithValue("sessions", NpgsqlDbType.Array | NpgsqlDbType.Varchar, sessions);
        command.Parameters.AddWithValue("selection", NpgsqlDbType.Char, selectionHash.ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sessionId = reader.GetString(0);
            var storedSelection = reader.GetString(1).Trim();
            var fence = reader.GetInt64(2);
            var intentId = reader.GetGuid(3);
            var state = reader.GetString(4);
            string? environmentId = reader.IsDBNull(5) ? null : reader.GetString(5);
            Guid? runtimeInstanceId = reader.IsDBNull(6) ? null : reader.GetGuid(6);
            var receiptLinks = reader.GetString(7);
            var cleanupObligation = reader.GetBoolean(8);
            var rowVersion = reader.GetInt64(9);
            if (!expected.TryGetValue(sessionId, out var saved) ||
                intentId != saved.IntentId || fence != currentFence || state != "ready" ||
                cleanupObligation || runtimeInstanceId is null || rowVersion < 1 ||
                !string.Equals(storedSelection, selectionHash, StringComparison.OrdinalIgnoreCase))
                throw new CoordinationException(
                    "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);

            using var links = JsonDocument.Parse(receiptLinks);
            if (!links.RootElement.TryGetProperty("checkpointRecoveryEvidenceOperationId", out var evidenceId) ||
                evidenceId.ValueKind != JsonValueKind.String ||
                evidenceId.GetGuid() != operationId ||
                !links.RootElement.TryGetProperty("checkpointRecoveryPreviousRegistration", out var priorValue) ||
                priorValue.Deserialize<RuntimeRegistration>(JsonOptions) is not { } prior ||
                prior.RuntimeInstanceId != saved.PreviousRuntimeId || prior.Revision != saved.PreviousRevision ||
                prior.State != RuntimeRegistrationState.Active ||
                RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(prior.Binding)) !=
                    saved.PreviousBindingHash ||
                !links.RootElement.TryGetProperty("checkpointRecoveryPreviousPlacement", out var priorPlacementValue) ||
                priorPlacementValue.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions) is not { } priorPlacement ||
                saved.PreviousPlacement != priorPlacement ||
                !links.RootElement.TryGetProperty("checkpointRecoveryPreviousBootstrapOperationId", out var priorBootstrapValue) ||
                priorBootstrapValue.ValueKind != JsonValueKind.String ||
                priorBootstrapValue.GetGuid() != saved.PreviousBootstrapOperationId ||
                !links.RootElement.TryGetProperty("checkpointRecoveryRetirement", out var retirementValue) ||
                retirementValue.Deserialize<RuntimeRunStartRetirementReceipt>(JsonOptions) is not { } retirement ||
                retirement.ContractVersion != (sessionId == identity.SessionId ? 1 : 2) ||
                retirement.BootstrapOperationId != saved.PreviousBootstrapOperationId ||
                retirement.RuntimeInstanceId != prior.RuntimeInstanceId ||
                retirement.RegistrationRevision != prior.Revision ||
                retirement.SandboxOperationId != priorPlacement.SandboxOperationId ||
                retirement.ResourceGeneration != priorPlacement.SandboxResource.Generation ||
                retirement.ProviderFencingGeneration != priorPlacement.ProviderFencingGeneration ||
                retirement.SandboxIdempotencyKey != priorPlacement.SandboxIdempotencyKey ||
                retirement.State != "released" ||
                !links.RootElement.TryGetProperty("registration", out var registrationValue) ||
                registrationValue.Deserialize<RuntimeRegistration>(JsonOptions) is not { } registration ||
                !links.RootElement.TryGetProperty("placement", out var placementValue) ||
                placementValue.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions) is not { } placement)
                throw new CoordinationException(
                    "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);
            RuntimeContractValidation.Validate(registration);
            placement.Validate();
            var turnIdentity = new SessionIdentity(identity.ProjectId, identity.RunId, sessionId);
            var turnOrdinal = sessionId == identity.SessionId
                ? 0
                : members.Single(member => member.Session.SessionId == sessionId).Session.LogicalTurnOrdinal;
            var expectedTurnId = turnOrdinal == 0
                ? CreateCoordinatorRootRuntimeTurnId(turnIdentity, currentFence)
                : CreateRuntimeTurnId(turnIdentity, currentFence, turnOrdinal);
            if (registration.State != RuntimeRegistrationState.Active ||
                registration.RuntimeInstanceId != RuntimeRegistrationStore.CreateReplacementId(
                    saved.PreviousRuntimeId, operationId) ||
                registration.RuntimeInstanceId != runtimeInstanceId ||
                registration.Revision != 1 ||
                ComputeRecoveryBindingIdentityHash(prior.Binding) !=
                    ComputeRecoveryBindingIdentityHash(registration.Binding) ||
                !RuntimeRunStartContinuation.IsResolvedProfileId(
                    prior.Binding.ProfileId, registration.Binding.ProfileId) ||
                registration.Binding.ProjectId != identity.ProjectId ||
                registration.Binding.RunId != identity.RunId ||
                registration.Binding.SessionId != sessionId ||
                registration.Binding.ExecutionFence != currentFence ||
                registration.Binding.TurnId != expectedTurnId ||
                !string.Equals(
                    registration.Binding.AcceptedSelectionHash, selectionHash, StringComparison.OrdinalIgnoreCase) ||
                placement.EnvironmentId != environmentId ||
                placement.EnvironmentId != registration.Binding.EnvironmentId ||
                placement.SourceProfileId != registration.Binding.ProfileId ||
                placement.SandboxResource.ResourceId != registration.Binding.PlacementUid ||
                placement.SandboxResource.Generation != registration.Binding.PlacementGeneration ||
                placement.SandboxResource.ProviderId != registration.Binding.PlacementProviderId ||
                placement.LifecycleGeneration != registration.Binding.EnvironmentLifecycleGeneration ||
                placement.LeaseRevision != registration.Binding.EnvironmentLeaseRevision ||
                placement.CurrentFencingGeneration != registration.Binding.EnvironmentCurrentFencingGeneration ||
                placement.ProviderFencingGeneration != registration.Binding.EnvironmentProviderFencingGeneration ||
                placement.SandboxOperationId == priorPlacement.SandboxOperationId ||
                placement.SandboxIdempotencyKey == priorPlacement.SandboxIdempotencyKey ||
                placement == priorPlacement)
                throw new CoordinationException(
                    "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);

            entries.Add(new
            {
                SessionId = sessionId,
                IntentId = intentId,
                RowVersion = rowVersion,
                Registration = registration,
                Placement = placement
            });
        }
        if (entries.Count != expected.Count)
            throw new CoordinationException(
                "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);

        return (entries.Count, RuntimeContractValidation.Hash(
            JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions)));
    }

    private static string ComputeRecoveryBindingIdentityHash(RuntimeBinding binding) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            binding with
            {
                TurnId = "",
                ExecutionFence = 0,
                EnvironmentId = "",
                PlacementUid = "",
                PlacementGeneration = 0,
                EnvironmentCurrentFencingGeneration = 0,
                EnvironmentProviderFencingGeneration = 0,
                EnvironmentLifecycleGeneration = 0,
                EnvironmentLeaseRevision = 0,
                ProfileId = "",
                ConfigureEndpoint = new Uri("https://recovery.invalid/"),
                ObservationEndpoint = new Uri("https://recovery.invalid/")
            }, JsonOptions));

    private async Task<RunExecutionTransitionResult> ApplyCheckpointRecoverySupersessionOverlayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RunExecutionTransitionResult operation,
        CancellationToken cancellationToken)
    {
        if (operation.CheckpointRecovery is not { BoundaryKind: "childJoin" } status)
            return operation;
        var receipt = await new RuntimeExecutionEvidenceLedgerStore(_schema[1..^1])
            .ReadCheckpointRecoverySupersessionAsync(
                connection, transaction, operation.Session.ProjectId, operation.Session.RunId,
                operation.OperationId, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
            return operation;

        var dispatchHeld = await CheckpointRecoveryHold.IsHeldAsync(
            connection, transaction, _schema, operation.Session.ProjectId, operation.Session.RunId,
            cancellationToken).ConfigureAwait(false);
        return operation with
        {
            CheckpointRecovery = status with
            {
                DispatchHeld = dispatchHeld,
                SupersededByOperationId = receipt.SuccessorOperationId,
                SupersededByReceiptId = receipt.ReceiptId,
                SupersededByPayloadHash = receipt.CanonicalPayloadHash
            }
        };
    }
}
