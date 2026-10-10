using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed record SessionSuspendResumeOperationSnapshot(
    Guid OperationId,
    SessionIdentity Identity,
    SessionSuspendResumeOperationKind Kind,
    SessionSuspendResumeOperationPhase Phase,
    long PhaseVersion,
    long OwnerExecutionFence,
    Guid ManifestId,
    string ManifestHash,
    SessionSuspendResumeManifest Manifest,
    JsonElement Progress,
    bool IsDuplicate);

internal sealed partial class CoordinationOwnerStore
{
    internal async Task RequireCurrentSessionSuspendAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionIdentity identity,
        Guid operationId,
        Guid manifestId,
        long executionFence,
        string acceptedSelectionHash,
        long phaseVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        if (operationId == Guid.Empty || manifestId == Guid.Empty ||
            executionFence < 1 || phaseVersion < 1 ||
            string.IsNullOrWhiteSpace(acceptedSelectionHash))
            throw new CoordinationException(
                "suspend_resume_proof_invalid", StatusCodes.Status400BadRequest);
        CoordinationIdentity.ValidateIdentity(identity.ProjectId, nameof(identity.ProjectId));
        CoordinationIdentity.ValidateIdentity(identity.RunId, nameof(identity.RunId));
        CoordinationIdentity.ValidateIdentity(identity.SessionId, nameof(identity.SessionId));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId,
            forUpdate: true, cancellationToken).ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireRunOwner(run, actor);
        RequireCurrentAuthorizedSelection(actor, selection, identity, run);
        RequireCurrentActiveSession(session, run);
        if (run.Fence != executionFence || session.ExecutionFence != executionFence ||
            run.SelectionHash != acceptedSelectionHash)
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        await using var command = new NpgsqlCommand($"""
            SELECT actor_issuer, actor_subject, operation_kind, idempotency_key, request_hash,
                operation_phase, phase_version, owner_execution_fence, reserved_manifest_id
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        var issuer = reader.GetString(0);
        var subject = reader.GetString(1);
        var kind = reader.GetString(2);
        var idempotencyKey = reader.GetString(3);
        var requestHash = reader.GetString(4).TrimEnd();
        var phase = SessionSuspendResumePhaseMachine.ParsePhase(reader.GetString(5));
        var storedPhaseVersion = reader.GetInt64(6);
        var storedFence = reader.GetInt64(7);
        var storedManifestId = reader.IsDBNull(8) ? Guid.Empty : reader.GetGuid(8);
        await reader.DisposeAsync().ConfigureAwait(false);

        if (issuer != actor.Issuer || subject != actor.Subject)
            throw new CoordinationException(
                "coordination_owner_forbidden", StatusCodes.Status403Forbidden);

        var expectedRequestHash = SessionSuspendResumePhaseMachine.HashRequest(
            SessionSuspendResumeOperationKind.Suspend,
            identity,
            new
            {
                idempotencyKey,
                sourceManifestId = (Guid?)null,
                acceptedSelectionHash
            });
        if (kind != "suspend" || storedManifestId != manifestId ||
            storedFence != executionFence || phase != SessionSuspendResumeOperationPhase.Fenced ||
            storedPhaseVersion != phaseVersion ||
            !string.Equals(requestHash, expectedRequestHash, StringComparison.Ordinal))
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SessionSuspendResumeOperationSnapshot> RecordUnavailableSessionSuspendResumeAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionIdentity identity,
        SessionSuspendResumeOperationKind kind,
        string idempotencyKey,
        Guid? sourceManifestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ValidateSessionSuspendResumeRequest(kind, idempotencyKey, sourceManifestId);
        CoordinationIdentity.ValidateIdentity(identity.ProjectId, nameof(identity.ProjectId));
        CoordinationIdentity.ValidateIdentity(identity.RunId, nameof(identity.RunId));
        CoordinationIdentity.ValidateIdentity(identity.SessionId, nameof(identity.SessionId));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId,
            forUpdate: true, cancellationToken).ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireRunOwner(run, actor);
        RequireCurrentAuthorizedSelection(actor, selection, identity, run);
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException(
                "execution_fence_stale", StatusCodes.Status409Conflict);

        var request = new
        {
            idempotencyKey,
            sourceManifestId,
            acceptedSelectionHash = run.SelectionHash
        };
        var requestHash = SessionSuspendResumePhaseMachine.HashRequest(kind, identity, request);

        var duplicate = await ReadExistingSessionSuspendResumeOperationAsync(
            connection, transaction, actor, identity, kind, idempotencyKey, requestHash, run.Fence,
            cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return duplicate with { IsDuplicate = true };
        }

        RequireCurrentActiveSession(session, run);
        if (run.ExecutionState == "completed" ||
            session.TurnState is "completed" or "failed" or "indeterminate")
            throw new CoordinationException(
                "suspend_resume_operation_conflict", StatusCodes.Status409Conflict);
        if (run.Fence <= 0)
            throw new CoordinationException(
                "suspend_resume_owner_fence_unavailable", StatusCodes.Status503ServiceUnavailable);

        var missingEvidence = await ReadMissingEvidenceAsync(
            connection, transaction, identity, run.Fence, kind, sourceManifestId,
            cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var manifestId = Guid.NewGuid();
        var manifest = CreateInterruptedManifest(identity, manifestId, missingEvidence);
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        var manifestHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson)));
        var progress = JsonSerializer.SerializeToElement(new
        {
            manifestId,
            sourceManifestId,
            missingEvidence
        }, JsonOptions);
        var result = JsonSerializer.SerializeToElement(new
        {
            error = "suspend_resume_owner_unavailable",
            operationId,
            manifestId,
            state = "interrupted"
        }, JsonOptions);

        await using (var insertOperation = new NpgsqlCommand($"""
            INSERT INTO {_executionOperations}
                (project_id, run_id, operation_id, session_id, reserved_manifest_id, actor_issuer, actor_subject,
                 operation_kind, idempotency_key, request_hash, result,
                 owner_execution_fence, operation_phase, phase_version, progress)
            VALUES
                (@project, @run, @operation, @session, @manifestId, @issuer, @subject,
                 @kind, @key, @hash, @empty,
                 @fence, 'reserved', 0, @empty)
            """, connection, transaction))
        {
            AddRunScope(insertOperation, identity.ProjectId, identity.RunId);
            insertOperation.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            insertOperation.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            insertOperation.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, manifestId);
            insertOperation.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insertOperation.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insertOperation.Parameters.AddWithValue(
                "kind", NpgsqlDbType.Varchar, SessionSuspendResumePhaseMachine.ToPersistenceValue(kind));
            insertOperation.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
            insertOperation.Parameters.AddWithValue("hash", NpgsqlDbType.Char, requestHash);
            insertOperation.Parameters.AddWithValue("empty", NpgsqlDbType.Jsonb, "{}");
            insertOperation.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, run.Fence);
            await insertOperation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insertManifest = new NpgsqlCommand($"""
            INSERT INTO {_schema}.session_consistency_manifests
                (project_id, run_id, session_id, operation_id, manifest_id, contract_version,
                 owner_execution_fence, claimed_execution_fence, manifest_state, manifest_hash, manifest)
            VALUES
                (@project, @run, @session, @operation, @manifestId, @contractVersion,
                 @ownerFence, @claimedFence, 'interrupted', @hash, @manifest)
            """, connection, transaction))
        {
            AddRunScope(insertManifest, identity.ProjectId, identity.RunId);
            insertManifest.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            insertManifest.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            insertManifest.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, manifestId);
            insertManifest.Parameters.AddWithValue(
                "contractVersion", NpgsqlDbType.Integer, manifest.ContractVersion);
            insertManifest.Parameters.AddWithValue("ownerFence", NpgsqlDbType.Bigint, run.Fence);
            insertManifest.Parameters.AddWithValue("claimedFence", NpgsqlDbType.Bigint, DBNull.Value);
            insertManifest.Parameters.AddWithValue("hash", NpgsqlDbType.Char, manifestHash);
            insertManifest.Parameters.AddWithValue("manifest", NpgsqlDbType.Jsonb, manifestJson);
            await insertManifest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var interruptOperation = new NpgsqlCommand($"""
            UPDATE {_executionOperations}
            SET operation_phase = 'interrupted',
                phase_version = 1,
                progress = @progress,
                result = @result
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation AND actor_issuer = @issuer AND actor_subject = @subject
                AND operation_kind = @kind AND idempotency_key = @key AND request_hash = @hash
                AND owner_execution_fence = @fence AND reserved_manifest_id = @manifestId
                AND operation_phase = 'reserved' AND phase_version = 0
            """, connection, transaction))
        {
            AddRunScope(interruptOperation, identity.ProjectId, identity.RunId);
            interruptOperation.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            interruptOperation.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            interruptOperation.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            interruptOperation.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            interruptOperation.Parameters.AddWithValue(
                "kind", NpgsqlDbType.Varchar, SessionSuspendResumePhaseMachine.ToPersistenceValue(kind));
            interruptOperation.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
            interruptOperation.Parameters.AddWithValue("hash", NpgsqlDbType.Char, requestHash);
            interruptOperation.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, run.Fence);
            interruptOperation.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, manifestId);
            interruptOperation.Parameters.AddWithValue(
                "progress", NpgsqlDbType.Jsonb, progress.GetRawText());
            interruptOperation.Parameters.AddWithValue(
                "result", NpgsqlDbType.Jsonb, result.GetRawText());
            if (await interruptOperation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CoordinationException(
                    "suspend_resume_operation_conflict", StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionSuspendResumeOperationSnapshot(
            operationId,
            identity,
            kind,
            SessionSuspendResumeOperationPhase.Interrupted,
            1,
            run.Fence,
            manifestId,
            manifestHash,
            manifest,
            progress,
            IsDuplicate: false);
    }

    private async Task<SessionSuspendResumeOperationSnapshot?> ReadExistingSessionSuspendResumeOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        SessionSuspendResumeOperationKind kind,
        string idempotencyKey,
        string requestHash,
        long currentExecutionFence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT o.operation_id, o.session_id, o.operation_kind, o.request_hash,
                o.operation_phase, o.phase_version, o.owner_execution_fence, o.progress,
                m.manifest_id, m.manifest_hash, m.manifest, m.owner_execution_fence,
                o.reserved_manifest_id
            FROM {_executionOperations} o
            LEFT JOIN {_schema}.session_consistency_manifests m
              ON m.project_id = o.project_id AND m.run_id = o.run_id
             AND m.session_id = o.session_id AND m.operation_id = o.operation_id
            WHERE o.project_id = @project AND o.run_id = @run
                AND o.actor_issuer = @issuer AND o.actor_subject = @subject
                AND o.idempotency_key = @key
            FOR UPDATE OF o
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var operationId = reader.GetGuid(0);
        var operationSessionId = reader.GetString(1);
        var operationKind = reader.GetString(2);
        var storedRequestHash = reader.GetString(3).TrimEnd();
        if (operationSessionId != identity.SessionId ||
            operationKind != SessionSuspendResumePhaseMachine.ToPersistenceValue(kind) ||
            storedRequestHash != requestHash)
            throw new CoordinationException(
                "execution_operation_idempotency_conflict", StatusCodes.Status409Conflict);

        var phase = SessionSuspendResumePhaseMachine.ParsePhase(reader.GetString(4));
        var phaseVersion = reader.GetInt64(5);
        var ownerFence = reader.GetInt64(6);
        if (ownerFence != currentExecutionFence)
            throw new CoordinationException(
                "suspend_resume_operation_fence_stale", StatusCodes.Status409Conflict);
        var progress = ReadJsonObject(reader.GetString(7), "suspend_resume_progress_invalid");
        if (reader.IsDBNull(8) || reader.IsDBNull(9) || reader.IsDBNull(10) ||
            reader.IsDBNull(11) || reader.IsDBNull(12))
            throw new CoordinationException(
                "suspend_resume_manifest_unavailable", StatusCodes.Status503ServiceUnavailable);

        var manifestId = reader.GetGuid(8);
        var manifestHash = reader.GetString(9).TrimEnd();
        if (reader.GetInt64(11) != ownerFence || reader.GetGuid(12) != manifestId)
            throw new CoordinationException(
                "suspend_resume_manifest_fence_invalid", StatusCodes.Status503ServiceUnavailable);
        var manifest = DeserializeAndValidateManifest(
            reader.GetString(10), manifestHash, identity, manifestId);
        RequireMatchingProgress(progress, manifestId, manifest);
        if (phase != SessionSuspendResumeOperationPhase.Interrupted)
            throw new CoordinationException(
                "suspend_resume_operation_in_progress", StatusCodes.Status503ServiceUnavailable);

        return new SessionSuspendResumeOperationSnapshot(
            operationId, identity, kind, phase, phaseVersion, ownerFence,
            manifestId, manifestHash, manifest, progress, IsDuplicate: true);
    }

    private async Task<ImmutableArray<string>> ReadMissingEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        long executionFence,
        SessionSuspendResumeOperationKind kind,
        Guid? sourceManifestId,
        CancellationToken cancellationToken)
    {
        if (kind == SessionSuspendResumeOperationKind.Suspend)
            return
            [
                "agenthost_drain_receipt_unavailable",
                "maf_checkpoint_snapshot_unavailable",
                "session_cache_acknowledgment_unavailable",
                "journal_flush_position_unavailable",
                "workspace_flush_generation_unavailable",
                "network_intent_generation_unavailable"
            ];

        var usableSourceManifest = false;
        if (sourceManifestId is { } manifestId)
        {
            await using var command = new NpgsqlCommand($"""
                SELECT run_id, session_id, owner_execution_fence, manifest_id, manifest_hash, manifest
                FROM {_schema}.session_consistency_manifests
                WHERE project_id = @project AND manifest_id = @manifestId
                FOR SHARE
                """, connection, transaction);
            command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
            command.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, manifestId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                reader.GetString(0) == identity.RunId &&
                reader.GetString(1) == identity.SessionId &&
                reader.GetGuid(3) == manifestId)
            {
                var storedOwnerFence = reader.GetInt64(2);
                var source = DeserializeAndValidateManifest(
                    reader.GetString(5), reader.GetString(4).TrimEnd(), identity, manifestId);
                usableSourceManifest = source.State == SessionSuspendResumeManifestState.Suspended &&
                    source.CoreExecutionFence == executionFence &&
                    storedOwnerFence == executionFence;
            }
        }

        var evidence = ImmutableArray.CreateBuilder<string>();
        if (!usableSourceManifest)
            evidence.Add("resume_manifest_missing_or_stale");
        evidence.Add("agenthost_restore_transport_unavailable");
        evidence.Add("maf_checkpoint_resume_transport_unavailable");
        evidence.Add("current_egress_proof_unavailable");
        evidence.Add("fenced_dispatch_transport_unavailable");
        return evidence.ToImmutable();
    }

    private static SessionSuspendResumeManifest CreateInterruptedManifest(
        SessionIdentity identity,
        Guid manifestId,
        ImmutableArray<string> missingEvidence) =>
        new SessionSuspendResumeManifest(
            SessionSuspendResumeContractVersions.CurrentManifestVersion,
            manifestId,
            identity,
            SessionSuspendResumeManifestState.Interrupted,
            EnvironmentFence: null,
            CoreExecutionFence: null,
            CacheAcknowledgment: null,
            Checkpoint: null,
            FlushedJournalPosition: null,
            WorkspaceVolume: null,
            WorkspaceResource: null,
            WorkspaceDataGeneration: null,
            WorkspaceTreeSha256: null,
            WorkspaceProviderCheckpointId: null,
            GuestSnapshotResource: null,
            GuestSnapshotLifecycleGeneration: null,
            NetworkIntentGeneration: null,
            missingEvidence)
        .Validate();

    private static SessionSuspendResumeManifest DeserializeAndValidateManifest(
        string json,
        string storedHash,
        SessionIdentity identity,
        Guid manifestId)
    {
        SessionSuspendResumeManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SessionSuspendResumeManifest>(json, JsonOptions);
        }
        catch (JsonException)
        {
            throw new CoordinationException(
                "suspend_resume_manifest_invalid", StatusCodes.Status503ServiceUnavailable);
        }

        if (manifest is null ||
            manifest.Identity != identity ||
            manifest.ManifestId != manifestId)
            throw new CoordinationException(
                "suspend_resume_manifest_invalid", StatusCodes.Status503ServiceUnavailable);

        try
        {
            manifest.Validate();
        }
        catch (ArgumentException)
        {
            throw new CoordinationException(
                "suspend_resume_manifest_invalid", StatusCodes.Status503ServiceUnavailable);
        }

        var canonical = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var canonicalHash = Convert.ToHexStringLower(SHA256.HashData(canonical));
        if (!string.Equals(canonicalHash, storedHash, StringComparison.Ordinal))
            throw new CoordinationException(
                "suspend_resume_manifest_integrity_invalid", StatusCodes.Status503ServiceUnavailable);
        return manifest;
    }

    private static JsonElement ReadJsonObject(string json, string errorCode)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new CoordinationException(errorCode, StatusCodes.Status503ServiceUnavailable);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new CoordinationException(errorCode, StatusCodes.Status503ServiceUnavailable);
            return document.RootElement.Clone();
        }
    }

    private static void RequireMatchingProgress(
        JsonElement progress,
        Guid manifestId,
        SessionSuspendResumeManifest manifest)
    {
        if (!progress.TryGetProperty("manifestId", out var storedManifestId) ||
            storedManifestId.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(storedManifestId.GetString(), out var parsedManifestId) ||
            parsedManifestId != manifestId ||
            !progress.TryGetProperty("missingEvidence", out var storedMissingEvidence) ||
            storedMissingEvidence.ValueKind != JsonValueKind.Array)
            throw new CoordinationException(
                "suspend_resume_progress_invalid", StatusCodes.Status503ServiceUnavailable);

        var storedGaps = ImmutableArray.CreateBuilder<string>();
        foreach (var item in storedMissingEvidence.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } evidence)
                throw new CoordinationException(
                    "suspend_resume_progress_invalid", StatusCodes.Status503ServiceUnavailable);
            storedGaps.Add(evidence);
        }
        if (!storedGaps.ToImmutable().SequenceEqual(manifest.MissingEvidence, StringComparer.Ordinal))
            throw new CoordinationException(
                "suspend_resume_progress_invalid", StatusCodes.Status503ServiceUnavailable);
    }

    private static void RequireCurrentAuthorizedSelection(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionIdentity identity,
        AcceptedRunRow run)
    {
        ValidateSelection(selection.Selection);
        var authorization = selection.Authorization;
        if (authorization.ContractVersion != 1 ||
            authorization.Issuer != actor.Issuer ||
            authorization.ActorId != actor.Subject ||
            authorization.TenantId != run.TenantId ||
            authorization.BoundProjectId != identity.ProjectId ||
            authorization.BoundRunId != identity.RunId ||
            authorization.MembershipRevision < 1 ||
            authorization.EffectiveAuthority.IsDefault ||
            !authorization.EffectiveAuthority.Any(authority =>
                authority.ResourceType == "project" &&
                authority.ResourceId == identity.ProjectId &&
                !authority.Permissions.IsDefault &&
                authority.Permissions.Any(permission =>
                    permission.Permission == "acceptRunSelection" &&
                    permission.RoleRevision > 0)))
            throw new CoordinationException(
                "run_selection_permission_denied", StatusCodes.Status403Forbidden);

        if (selection.Selection.ProjectId != identity.ProjectId ||
            selection.Selection.RunId != identity.RunId ||
            HashSelection(selection.Selection) != run.SelectionHash)
            throw new CoordinationException(
                "accepted_run_selection_stale", StatusCodes.Status409Conflict);
    }

    private static void ValidateSessionSuspendResumeRequest(
        SessionSuspendResumeOperationKind kind,
        string idempotencyKey,
        Guid? sourceManifestId)
    {
        if (!Enum.IsDefined(kind) ||
            string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Length > 128 ||
            idempotencyKey.Any(char.IsControl) ||
            kind == SessionSuspendResumeOperationKind.Suspend && sourceManifestId is not null ||
            kind == SessionSuspendResumeOperationKind.Resume &&
                (sourceManifestId is null || sourceManifestId == Guid.Empty))
            throw new CoordinationException(
                "suspend_resume_request_invalid", StatusCodes.Status400BadRequest);
        CoordinationIdentity.ValidateIdentity(idempotencyKey, "idempotency_key");
    }
}
