using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
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

internal sealed record SessionSuspendResumeOperationRecord(
    Guid OperationId,
    SessionIdentity Identity,
    SessionSuspendResumeOperationKind Kind,
    SessionSuspendResumeOperationPhase Phase,
    long PhaseVersion,
    long OwnerExecutionFence,
    Guid ManifestId,
    string IdempotencyKey,
    string RequestHash,
    JsonElement Progress,
    SessionSuspendResumeManifest? Manifest,
    string? ManifestHash,
    bool IsDuplicate);

internal sealed record SessionSuspendResumeSourceEvidence(
    SessionSuspendResumeManifest Manifest,
    string ManifestHash,
    RuntimeSuspendPlacementReleaseRequest PlacementRelease);

internal sealed partial class CoordinationOwnerStore
{
    internal async Task RequireCurrentSessionSuspendAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeHostSuspendRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(request);
        var proof = request.Proof;
        var registration = proof?.Registration;
        var binding = registration?.Binding;
        if (request.OperationId == Guid.Empty || request.ManifestId == Guid.Empty ||
            request.PhaseVersion < 1 || proof is null || registration is null || binding is null ||
            proof.ContractVersion != 1 || proof.SourceGrantId == Guid.Empty ||
            proof.SourceGrantRevision < 1 || proof.Purpose != RuntimeCredentialPurpose.Observe)
            throw new CoordinationException(
                "suspend_resume_proof_invalid", StatusCodes.Status400BadRequest);
        var identity = new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId);
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
        if (run.Fence != binding.ExecutionFence || session.ExecutionFence != binding.ExecutionFence ||
            run.SelectionHash != binding.AcceptedSelectionHash ||
            binding.ActorIssuer != actor.Issuer ||
            binding.ActorId != actor.Subject ||
            binding.TenantId != selection.Authorization.TenantId)
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        await using var command = new NpgsqlCommand($"""
            SELECT actor_issuer, actor_subject, operation_kind, idempotency_key, request_hash,
                operation_phase, phase_version, owner_execution_fence, reserved_manifest_id, progress::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, request.OperationId);
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
        var storedProgress = reader.GetString(9);
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
                acceptedSelectionHash = binding.AcceptedSelectionHash
            });
        var isResolve = request.WorkspaceFlush is null;
        var validPhase = isResolve
            ? phase == SessionSuspendResumeOperationPhase.Reserved && storedPhaseVersion == 0 &&
                request.PhaseVersion == 1
            : phase == SessionSuspendResumeOperationPhase.Fenced &&
                storedPhaseVersion == request.PhaseVersion;
        if (kind != "suspend" || storedManifestId != request.ManifestId ||
            storedFence != binding.ExecutionFence || !validPhase ||
            !string.Equals(requestHash, expectedRequestHash, StringComparison.Ordinal))
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        if (!isResolve)
        {
            var progress = DeserializeSessionSuspendResumeProgress(storedProgress);
            if (progress.ResolvedRequest is null ||
                !SessionSuspendResumePhaseMachine.SameJson(progress.ResolvedRequest.Suspend, request))
                throw new CoordinationException(
                    "suspend_resume_operation_stale", StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task RequireCurrentSessionSuspendReleaseAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeSuspendPlacementReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            request.Validate();
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                exception.Code, StatusCodes.Status400BadRequest, exception);
        }

        var binding = request.HostReceipt.Registration.Binding;
        var identity = new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId);
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

        var registration = request.HostReceipt.Registration;
        var operationId = request.HostReceipt.OperationId;
        var ownerFence = binding.ExecutionFence;
        if (request.HostReceipt.ManifestId == Guid.Empty ||
            operationId == Guid.Empty ||
            run.Fence != ownerFence ||
            session.ExecutionFence != ownerFence ||
            run.SelectionHash != binding.AcceptedSelectionHash ||
            binding.ActorIssuer != actor.Issuer ||
            binding.ActorId != actor.Subject ||
            binding.TenantId != selection.Authorization.TenantId)
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        string issuer;
        string subject;
        string kind;
        string idempotencyKey;
        string requestHash;
        SessionSuspendResumeOperationPhase phase;
        long phaseVersion;
        long storedFence;
        Guid manifestId;
        JsonElement progress;
        await using (var command = new NpgsqlCommand($"""
            SELECT actor_issuer, actor_subject, operation_kind, idempotency_key, request_hash,
                operation_phase, phase_version, owner_execution_fence, reserved_manifest_id, progress::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation
            FOR UPDATE
            """, connection, transaction))
        {
            AddRunScope(command, identity.ProjectId, identity.RunId);
            command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException(
                    "suspend_resume_operation_stale", StatusCodes.Status409Conflict);
            issuer = reader.GetString(0);
            subject = reader.GetString(1);
            kind = reader.GetString(2);
            idempotencyKey = reader.GetString(3);
            requestHash = reader.GetString(4).TrimEnd();
            phase = SessionSuspendResumePhaseMachine.ParsePhase(reader.GetString(5));
            phaseVersion = reader.GetInt64(6);
            storedFence = reader.GetInt64(7);
            manifestId = reader.IsDBNull(8) ? Guid.Empty : reader.GetGuid(8);
            progress = ReadJsonObject(reader.GetString(9), "suspend_resume_progress_invalid");
        }

        var expectedRequestHash = SessionSuspendResumePhaseMachine.HashRequest(
            SessionSuspendResumeOperationKind.Suspend,
            identity,
            new
            {
                idempotencyKey,
                sourceManifestId = (Guid?)null,
                acceptedSelectionHash = binding.AcceptedSelectionHash
            });
        if (issuer != actor.Issuer || subject != actor.Subject ||
            kind != SessionSuspendResumePhaseMachine.ToPersistenceValue(
                SessionSuspendResumeOperationKind.Suspend) ||
            phase is not (SessionSuspendResumeOperationPhase.ManifestCommitted or
                SessionSuspendResumeOperationPhase.PlacementReleased) ||
            storedFence != ownerFence || manifestId != request.HostReceipt.ManifestId ||
            !string.Equals(requestHash, expectedRequestHash, StringComparison.Ordinal))
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        SessionSuspendResumeManifest manifest;
        string manifestHash;
        await using (var command = new NpgsqlCommand($"""
            SELECT manifest_id, manifest_hash, manifest, owner_execution_fence, claimed_execution_fence
            FROM {_schema}.session_consistency_manifests
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation
            FOR SHARE
            """, connection, transaction))
        {
            AddRunScope(command, identity.ProjectId, identity.RunId);
            command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetGuid(0) != manifestId ||
                reader.GetInt64(3) != ownerFence ||
                reader.IsDBNull(4) ||
                reader.GetInt64(4) != ownerFence)
                throw new CoordinationException(
                    "suspend_resume_manifest_unavailable", StatusCodes.Status503ServiceUnavailable);
            manifestHash = reader.GetString(1).TrimEnd();
            manifest = DeserializeAndValidateManifest(
                reader.GetString(2), manifestHash, identity, manifestId);
            if (manifest.State != SessionSuspendResumeManifestState.Suspended)
                throw new CoordinationException(
                    "suspend_resume_manifest_invalid", StatusCodes.Status503ServiceUnavailable);
        }

        var operation = new SessionSuspendResumeOperationRecord(
            operationId,
            identity,
            SessionSuspendResumeOperationKind.Suspend,
            phase,
            phaseVersion,
            ownerFence,
            manifestId,
            idempotencyKey,
            requestHash,
            progress,
            manifest,
            manifestHash,
            IsDuplicate: true);
        var saved = SessionSuspendResumeCoordinator.RequireCommittedProgress(operation);
        var expectedRelease = SessionSuspendResumeCoordinator.CreatePlacementReleaseRequest(saved);
        if (!SessionSuspendResumePhaseMachine.SameJson(expectedRelease, request))
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task RequireCurrentSessionResumeAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeWorkspaceResumeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            request.Validate();
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                exception.Code, StatusCodes.Status400BadRequest, exception);
        }

        var identity = request.SourceManifest.Identity;
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
        if (run.ExecutionState == "completed" ||
            session.TurnState is "completed" or "failed" or "indeterminate" ||
            run.Fence != session.ExecutionFence ||
            run.Fence != request.SourceManifest.CoreExecutionFence ||
            run.SelectionHash != request.SourceRelease.HostReceipt.Registration.Binding.AcceptedSelectionHash)
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        string issuer;
        string subject;
        string kind;
        string idempotencyKey;
        string requestHash;
        SessionSuspendResumeOperationPhase phase;
        long phaseVersion;
        long ownerFence;
        Guid manifestId;
        JsonElement progress;
        await using (var command = new NpgsqlCommand($"""
            SELECT actor_issuer, actor_subject, operation_kind, idempotency_key, request_hash,
                operation_phase, phase_version, owner_execution_fence, reserved_manifest_id, progress::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation
            FOR UPDATE
            """, connection, transaction))
        {
            AddRunScope(command, identity.ProjectId, identity.RunId);
            command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, request.OperationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException(
                    "suspend_resume_operation_stale", StatusCodes.Status409Conflict);
            issuer = reader.GetString(0);
            subject = reader.GetString(1);
            kind = reader.GetString(2);
            idempotencyKey = reader.GetString(3);
            requestHash = reader.GetString(4).TrimEnd();
            phase = SessionSuspendResumePhaseMachine.ParsePhase(reader.GetString(5));
            phaseVersion = reader.GetInt64(6);
            ownerFence = reader.GetInt64(7);
            manifestId = reader.IsDBNull(8) ? Guid.Empty : reader.GetGuid(8);
            progress = ReadJsonObject(reader.GetString(9), "suspend_resume_progress_invalid");
        }

        var expectedHash = SessionSuspendResumePhaseMachine.HashRequest(
            SessionSuspendResumeOperationKind.Resume,
            identity,
            new
            {
                idempotencyKey,
                sourceManifestId = (Guid?)request.SourceManifest.ManifestId,
                acceptedSelectionHash = run.SelectionHash
            });
        if (issuer != actor.Issuer || subject != actor.Subject || kind != "resume" ||
            manifestId != request.SourceManifest.ManifestId || ownerFence != run.Fence ||
            requestHash != expectedHash)
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        var saved = DeserializeSessionSuspendResumeProgress(progress.GetRawText());
        var source = await ReadResumeSourceEvidenceAsync(
            connection, transaction, actor, identity, request.SourceManifest.ManifestId,
            run.Fence, run.Fence, cancellationToken).ConfigureAwait(false);
        if (!SessionSuspendResumePhaseMachine.SameJson(source.Manifest, request.SourceManifest) ||
            !SessionSuspendResumePhaseMachine.SameJson(source.PlacementRelease, request.SourceRelease) ||
            saved.ResumeRequest is not { } savedRequest ||
            !SessionSuspendResumePhaseMachine.SameJson(
                savedRequest with { PhaseVersion = request.PhaseVersion, RestoredWorkspace = null },
                request with { RestoredWorkspace = null }))
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        var nextPhase = checked(phaseVersion + 1);
        var validPrepare = phase == SessionSuspendResumeOperationPhase.ManifestValidated &&
            request.PhaseVersion == nextPhase && request.RestoredWorkspace is null &&
            saved.ResumePlacementReceipt is null && saved.ResumeVerificationReceipt is null;
        var validVerify = phase == SessionSuspendResumeOperationPhase.EnvironmentReady &&
            request.PhaseVersion == nextPhase && request.RestoredWorkspace is not null &&
            saved.ResumePlacementReceipt is { } placement &&
            saved.ResumeVerificationReceipt is null &&
            SessionSuspendResumePhaseMachine.SameJson(placement.Workspace, request.RestoredWorkspace);
        if (!validPrepare && !validVerify)
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task RequireCurrentSessionResumeBootstrapAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeResumeBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            request.Validate();
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                exception.Code, StatusCodes.Status400BadRequest, exception);
        }

        var identity = request.Resume.SourceManifest.Identity;
        var binding = request.Registration.Binding;
        CoordinationIdentity.ValidateIdentity(identity.ProjectId, nameof(identity.ProjectId));
        CoordinationIdentity.ValidateIdentity(identity.RunId, nameof(identity.RunId));
        CoordinationIdentity.ValidateIdentity(identity.SessionId, nameof(identity.SessionId));
        if (identity != new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId))
            throw new CoordinationException(
                "runtime_resume_bootstrap_scope_invalid", StatusCodes.Status403Forbidden);

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
        if (run.ExecutionState == "completed" ||
            session.TurnState is "completed" or "failed" or "indeterminate" ||
            run.Fence != session.ExecutionFence || run.Fence <= 1 ||
            run.SelectionHash != binding.AcceptedSelectionHash ||
            binding.ActorIssuer != actor.Issuer || binding.ActorId != actor.Subject ||
            binding.TenantId != selection.Authorization.TenantId ||
            binding.ExecutionFence != run.Fence ||
            request.Resume.SourceManifest.CoreExecutionFence != run.Fence - 1)
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        string issuer;
        string subject;
        string kind;
        string idempotencyKey;
        string requestHash;
        SessionSuspendResumeOperationPhase phase;
        long phaseVersion;
        long ownerFence;
        Guid manifestId;
        JsonElement progressJson;
        await using (var command = new NpgsqlCommand($"""
            SELECT actor_issuer, actor_subject, operation_kind, idempotency_key, request_hash,
                operation_phase, phase_version, owner_execution_fence, reserved_manifest_id, progress::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation
            FOR UPDATE
            """, connection, transaction))
        {
            AddRunScope(command, identity.ProjectId, identity.RunId);
            command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, request.Resume.OperationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException(
                    "suspend_resume_operation_stale", StatusCodes.Status409Conflict);
            issuer = reader.GetString(0);
            subject = reader.GetString(1);
            kind = reader.GetString(2);
            idempotencyKey = reader.GetString(3);
            requestHash = reader.GetString(4).TrimEnd();
            phase = SessionSuspendResumePhaseMachine.ParsePhase(reader.GetString(5));
            phaseVersion = reader.GetInt64(6);
            ownerFence = reader.GetInt64(7);
            manifestId = reader.IsDBNull(8) ? Guid.Empty : reader.GetGuid(8);
            progressJson = ReadJsonObject(reader.GetString(9), "suspend_resume_progress_invalid");
        }

        var expectedRequestHash = SessionSuspendResumePhaseMachine.HashRequest(
            SessionSuspendResumeOperationKind.Resume,
            identity,
            new
            {
                idempotencyKey,
                sourceManifestId = (Guid?)request.Resume.SourceManifest.ManifestId,
                acceptedSelectionHash = run.SelectionHash
            });
        if (issuer != actor.Issuer || subject != actor.Subject || kind != "resume" ||
            phase != SessionSuspendResumeOperationPhase.FenceAdvanced || phaseVersion != 4 ||
            ownerFence != run.Fence || manifestId != request.Resume.SourceManifest.ManifestId ||
            requestHash != expectedRequestHash)
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        var progress = DeserializeSessionSuspendResumeProgress(progressJson.GetRawText());
        var savedResume = progress.ResumeRequest
            ?? throw new CoordinationException(
                "runtime_resume_bootstrap_progress_invalid", StatusCodes.Status503ServiceUnavailable);
        var savedPlacement = progress.ResumePlacementReceipt
            ?? throw new CoordinationException(
                "runtime_resume_bootstrap_progress_invalid", StatusCodes.Status503ServiceUnavailable);
        var savedVerification = progress.ResumeVerificationReceipt
            ?? throw new CoordinationException(
                "runtime_resume_bootstrap_progress_invalid", StatusCodes.Status503ServiceUnavailable);
        if (progress.ResumeExecutionFence != run.Fence || savedResume.PhaseVersion != 3 ||
            savedResume.RestoredWorkspace is null ||
            !SessionSuspendResumePhaseMachine.SameJson(
                savedResume with { PhaseVersion = 4 }, request.Resume) ||
            !SessionSuspendResumePhaseMachine.SameJson(
                savedPlacement.Workspace, request.Resume.RestoredWorkspace) ||
            !SessionSuspendResumePhaseMachine.SameJson(
                savedVerification.Workspace, request.Resume.RestoredWorkspace))
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        var source = await ReadResumeSourceEvidenceAsync(
            connection, transaction, actor, identity, request.Resume.SourceManifest.ManifestId,
            run.Fence - 1, run.Fence, cancellationToken)
            .ConfigureAwait(false);
        if (!SessionSuspendResumePhaseMachine.SameJson(source.Manifest, request.Resume.SourceManifest) ||
            !SessionSuspendResumePhaseMachine.SameJson(source.PlacementRelease, request.Resume.SourceRelease))
            throw new CoordinationException(
                "suspend_resume_manifest_stale", StatusCodes.Status409Conflict);
        try
        {
            savedResume.Validate();
            savedPlacement.ValidateFor(savedResume);
            savedVerification.ValidateFor(savedResume);
            var ids = SessionSuspendResumePhaseMachine.ResumeBootstrapOperationIds(request.Resume.OperationId);
            if (request.BootstrapOperationId != ids.Bootstrap ||
                request.ConsumeOperationId != ids.Consume ||
                request.ExchangeOperationId != ids.Exchange)
                throw new RuntimeAuthorizationException("runtime_resume_bootstrap_operation_id_invalid");
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                exception.Code, StatusCodes.Status409Conflict, exception);
        }

        await RequireCurrentResumeBootstrapRegistrationInTransactionAsync(
            connection, transaction, request.Registration, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireCurrentResumeBootstrapRegistrationInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeRegistration expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        await using var command = new NpgsqlCommand($"""
            SELECT revision.runtime_instance_id, revision.revision, revision.binding_json,
                   revision.binding_hash, revision.state, revision.expires_at
            FROM {_schema}.runtime_registration_heads AS head
            JOIN {_schema}.runtime_registration_revisions AS revision
              ON revision.runtime_instance_id = head.runtime_instance_id
             AND revision.revision = head.current_revision
            WHERE head.runtime_instance_id = @runtime
            FOR SHARE OF head
            """, connection, transaction);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, expected.RuntimeInstanceId);
        RuntimeRegistration? current;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            current = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? RuntimeRegistrationStore.ReadRegistration(reader)
                : null;
        if (current is null || current != expected ||
            current.State != RuntimeRegistrationState.Active ||
            current.ExpiresAt <= _timeProvider.GetUtcNow())
            throw new CoordinationException(
                "runtime_registration_stale", StatusCodes.Status409Conflict);
    }

    internal async Task<SessionSuspendResumeOperationRecord> BeginSessionResumeAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionIdentity identity,
        string idempotencyKey,
        Guid sourceManifestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ValidateSessionSuspendResumeRequest(
            SessionSuspendResumeOperationKind.Resume, idempotencyKey, sourceManifestId);
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
        if (run.ExecutionState == "completed" ||
            session.TurnState is "completed" or "failed" or "indeterminate" ||
            run.Fence <= 0)
            throw new CoordinationException(
                "suspend_resume_operation_conflict", StatusCodes.Status409Conflict);

        var requestHash = SessionSuspendResumePhaseMachine.HashRequest(
            SessionSuspendResumeOperationKind.Resume,
            identity,
            new
            {
                idempotencyKey,
                sourceManifestId = (Guid?)sourceManifestId,
                acceptedSelectionHash = run.SelectionHash
            });
        var duplicate = await ReadExistingSessionSuspendResumeOperationRecordAsync(
            connection, transaction, actor, identity, SessionSuspendResumeOperationKind.Resume,
            idempotencyKey, requestHash, run.Fence, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return duplicate with { IsDuplicate = true };
        }

        var source = await ReadResumeSourceEvidenceAsync(
            connection, transaction, actor, identity, sourceManifestId, run.Fence, run.Fence,
            cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var initialRequest = new RuntimeWorkspaceResumeRequest(
            1, operationId, 1, source.Manifest, source.PlacementRelease).Validate();
        var progress = SerializeSessionSuspendResumeProgress(
            SessionSuspendResumeProgress.Empty with { ResumeRequest = initialRequest });
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_executionOperations}
                (project_id, run_id, operation_id, session_id, reserved_manifest_id, actor_issuer, actor_subject,
                 operation_kind, idempotency_key, request_hash, result,
                 owner_execution_fence, operation_phase, phase_version, progress)
            VALUES
                (@project, @run, @operation, @session, @manifestId, @issuer, @subject,
                 'resume', @key, @hash, @empty,
                 @fence, 'reserved', 0, @progress)
            """, connection, transaction))
        {
            AddRunScope(insert, identity.ProjectId, identity.RunId);
            insert.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            insert.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, sourceManifestId);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, requestHash);
            insert.Parameters.AddWithValue("empty", NpgsqlDbType.Jsonb, "{}");
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, run.Fence);
            insert.Parameters.AddWithValue("progress", NpgsqlDbType.Jsonb, progress.GetRawText());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            operationId,
            identity,
            SessionSuspendResumeOperationKind.Resume,
            SessionSuspendResumeOperationPhase.Reserved,
            0,
            run.Fence,
            sourceManifestId,
            idempotencyKey,
            requestHash,
            progress,
            Manifest: null,
            ManifestHash: null,
            IsDuplicate: false);
    }

    internal async Task<SessionSuspendResumeOperationRecord> AdvanceSessionResumeFenceAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionSuspendResumeOperationRecord operation,
        SessionSuspendResumeProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var request = progress.ResumeRequest;
        if (operation.Kind != SessionSuspendResumeOperationKind.Resume ||
            operation.Phase != SessionSuspendResumeOperationPhase.EgressVerified ||
            request?.RestoredWorkspace is null ||
            progress.ResumeVerificationReceipt is not { } verification ||
            progress.ResumePlacementReceipt is null)
            throw new CoordinationException(
                "suspend_resume_operation_transition_invalid", StatusCodes.Status409Conflict);
        try
        {
            request.Validate();
            verification.ValidateFor(request);
            progress.ResumePlacementReceipt!.ValidateFor(request);
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                exception.Code, StatusCodes.Status409Conflict, exception);
        }

        var serializedProgress = SerializeSessionSuspendResumeProgress(progress);
        RequireBoundedProgress(serializedProgress);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await RequireCurrentSessionSuspendOperationInTransactionAsync(
            connection, transaction, actor, selection, operation, cancellationToken).ConfigureAwait(false);

        var run = await ReadAcceptedRunAsync(
            connection, transaction, operation.Identity.ProjectId, operation.Identity.RunId,
            forUpdate: true, cancellationToken).ConfigureAwait(false);
        var previousFence = operation.OwnerExecutionFence;
        var source = await ReadResumeSourceEvidenceAsync(
            connection, transaction, actor, operation.Identity, operation.ManifestId,
            previousFence, previousFence, cancellationToken).ConfigureAwait(false);
        if (!SessionSuspendResumePhaseMachine.SameJson(source.Manifest, request.SourceManifest) ||
            !SessionSuspendResumePhaseMachine.SameJson(source.PlacementRelease, request.SourceRelease) ||
            progress.ResumeExecutionFence is not null)
            throw new CoordinationException(
                "suspend_resume_manifest_stale", StatusCodes.Status409Conflict);
        var nextFence = checked(previousFence + 1);
        var expectedSessionCount = await ReadRunSessionCountAtFenceAsync(
            connection, transaction, operation.Identity.ProjectId, operation.Identity.RunId,
            previousFence, cancellationToken).ConfigureAwait(false);
        var updatedSessions = new HashSet<string>(StringComparer.Ordinal);
        await using (var updateSessions = new NpgsqlCommand($"""
            UPDATE {_sessions}
            SET execution_fence = @nextFence, state_version = state_version + 1
            WHERE project_id = @project AND run_id = @run AND execution_fence = @previousFence
            RETURNING session_id
            """, connection, transaction))
        {
            AddRunScope(updateSessions, operation.Identity.ProjectId, operation.Identity.RunId);
            updateSessions.Parameters.AddWithValue("previousFence", NpgsqlDbType.Bigint, previousFence);
            updateSessions.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, nextFence);
            await using var reader = await updateSessions.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                updatedSessions.Add(reader.GetString(0));
        }
        if (updatedSessions.Count != expectedSessionCount ||
            !updatedSessions.Contains(operation.Identity.SessionId))
            throw new CoordinationException(
                "execution_fence_inconsistent", StatusCodes.Status503ServiceUnavailable);

        await using (var updateRun = new NpgsqlCommand($"""
            UPDATE {_runs}
            SET execution_fence = @nextFence, state_version = state_version + 1,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run
                AND execution_fence = @previousFence AND state_version = @runVersion
                AND execution_state <> 'completed'
            """, connection, transaction))
        {
            AddRunScope(updateRun, operation.Identity.ProjectId, operation.Identity.RunId);
            updateRun.Parameters.AddWithValue("previousFence", NpgsqlDbType.Bigint, previousFence);
            updateRun.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, nextFence);
            updateRun.Parameters.AddWithValue("runVersion", NpgsqlDbType.Bigint, run.StateVersion);
            if (await updateRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CoordinationException(
                    "suspend_resume_operation_stale", StatusCodes.Status409Conflict);
        }

        await using (var claimManifest = new NpgsqlCommand($"""
            UPDATE {_schema}.session_consistency_manifests
            SET claimed_execution_fence = @nextFence
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND manifest_id = @manifestId AND manifest_state = 'suspended'
                AND owner_execution_fence = @previousFence
                AND claimed_execution_fence = @previousFence
            """, connection, transaction))
        {
            AddRunScope(claimManifest, operation.Identity.ProjectId, operation.Identity.RunId);
            claimManifest.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, operation.Identity.SessionId);
            claimManifest.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, operation.ManifestId);
            claimManifest.Parameters.AddWithValue("previousFence", NpgsqlDbType.Bigint, previousFence);
            claimManifest.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, nextFence);
            if (await claimManifest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CoordinationException(
                    "suspend_resume_manifest_stale", StatusCodes.Status409Conflict);
        }

        await AdvanceCurrentDecisionFenceAsync(
            connection,
            transaction,
            actor,
            operation.Identity,
            selection,
            previousFence,
            nextFence,
            operation.OperationId,
            "execution.recovered",
            cancellationToken).ConfigureAwait(false);

        var savedProgress = progress with { ResumeExecutionFence = nextFence };
        serializedProgress = SerializeSessionSuspendResumeProgress(savedProgress);
        RequireBoundedProgress(serializedProgress);
        var nextVersion = checked(operation.PhaseVersion + 1);
        await using (var updateOperation = new NpgsqlCommand($"""
            UPDATE {_executionOperations}
            SET operation_phase = 'fence_advanced', phase_version = @nextVersion,
                owner_execution_fence = @nextFence, progress = @progress, result = @result
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation AND actor_issuer = @issuer AND actor_subject = @subject
                AND operation_kind = 'resume' AND idempotency_key = @key AND request_hash = @hash
                AND owner_execution_fence = @previousFence AND reserved_manifest_id = @manifestId
                AND operation_phase = 'egress_verified' AND phase_version = @currentVersion
            """, connection, transaction))
        {
            AddRunScope(updateOperation, operation.Identity.ProjectId, operation.Identity.RunId);
            updateOperation.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, operation.Identity.SessionId);
            updateOperation.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operation.OperationId);
            updateOperation.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            updateOperation.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            updateOperation.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, operation.IdempotencyKey);
            updateOperation.Parameters.AddWithValue("hash", NpgsqlDbType.Char, operation.RequestHash);
            updateOperation.Parameters.AddWithValue("previousFence", NpgsqlDbType.Bigint, previousFence);
            updateOperation.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, nextFence);
            updateOperation.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, operation.ManifestId);
            updateOperation.Parameters.AddWithValue("currentVersion", NpgsqlDbType.Bigint, operation.PhaseVersion);
            updateOperation.Parameters.AddWithValue("nextVersion", NpgsqlDbType.Bigint, nextVersion);
            updateOperation.Parameters.AddWithValue("progress", NpgsqlDbType.Jsonb, serializedProgress.GetRawText());
            updateOperation.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new
            {
                state = "fence_advanced",
                operationId = operation.OperationId,
                manifestId = operation.ManifestId,
                executionFence = nextFence
            }, JsonOptions));
            if (await updateOperation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CoordinationException(
                    "suspend_resume_operation_conflict", StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return operation with
        {
            Phase = SessionSuspendResumeOperationPhase.FenceAdvanced,
            PhaseVersion = nextVersion,
            OwnerExecutionFence = nextFence,
            Progress = serializedProgress,
            IsDuplicate = false
        };
    }

    private async Task<SessionSuspendResumeSourceEvidence> ReadResumeSourceEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        Guid sourceManifestId,
        long expectedManifestOwnerFence,
        long expectedClaimedFence,
        CancellationToken cancellationToken)
    {
        Guid operationId;
        string manifestHash;
        SessionSuspendResumeManifest manifest;
        string operationIssuer;
        string operationSubject;
        string operationKind;
        string suspendIdempotencyKey;
        string suspendRequestHash;
        SessionSuspendResumeOperationPhase phase;
        long phaseVersion;
        long operationFence;
        Guid operationManifestId;
        JsonElement progress;
        await using (var command = new NpgsqlCommand($"""
            SELECT m.run_id, m.session_id, m.operation_id, m.manifest_hash, m.manifest,
                m.owner_execution_fence, m.claimed_execution_fence,
                o.actor_issuer, o.actor_subject, o.operation_kind, o.idempotency_key, o.request_hash,
                o.operation_phase, o.phase_version, o.owner_execution_fence,
                o.reserved_manifest_id, o.progress::text
            FROM {_schema}.session_consistency_manifests m
            JOIN {_executionOperations} o
              ON o.project_id = m.project_id AND o.run_id = m.run_id
             AND o.session_id = m.session_id AND o.operation_id = m.operation_id
            WHERE m.project_id = @project AND m.run_id = @run AND m.session_id = @session
                AND m.manifest_id = @manifestId
            FOR SHARE OF m, o
            """, connection, transaction))
        {
            AddRunScope(command, identity.ProjectId, identity.RunId);
            command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            command.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, sourceManifestId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != identity.RunId ||
                reader.GetString(1) != identity.SessionId ||
                !SessionSuspendResumePhaseMachine.MatchesManifestFencePair(
                    reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    expectedManifestOwnerFence,
                    expectedClaimedFence))
                throw new CoordinationException(
                    "suspend_resume_manifest_unavailable", StatusCodes.Status409Conflict);

            operationId = reader.GetGuid(2);
            manifestHash = reader.GetString(3).TrimEnd();
            manifest = DeserializeAndValidateManifest(
                reader.GetString(4), manifestHash, identity, sourceManifestId);
            operationIssuer = reader.GetString(7);
            operationSubject = reader.GetString(8);
            operationKind = reader.GetString(9);
            suspendIdempotencyKey = reader.GetString(10);
            suspendRequestHash = reader.GetString(11).TrimEnd();
            phase = SessionSuspendResumePhaseMachine.ParsePhase(reader.GetString(12));
            phaseVersion = reader.GetInt64(13);
            operationFence = reader.GetInt64(14);
            operationManifestId = reader.IsDBNull(15) ? Guid.Empty : reader.GetGuid(15);
            progress = ReadJsonObject(reader.GetString(16), "suspend_resume_progress_invalid");
        }

        var binding = manifest.EnvironmentFence is null
            ? null
            : manifest.EnvironmentFence.Owner;
        if (binding is null ||
            operationIssuer != actor.Issuer || operationSubject != actor.Subject ||
            operationKind != "suspend" ||
            phase != SessionSuspendResumeOperationPhase.PlacementReleased ||
            operationFence != expectedManifestOwnerFence ||
            operationManifestId != sourceManifestId ||
            manifest.State != SessionSuspendResumeManifestState.Suspended ||
            manifest.CoreExecutionFence != expectedManifestOwnerFence)
            throw new CoordinationException(
                "suspend_resume_manifest_unavailable", StatusCodes.Status409Conflict);

        var expectedRequestHash = SessionSuspendResumePhaseMachine.HashRequest(
            SessionSuspendResumeOperationKind.Suspend,
            identity,
            new
            {
                idempotencyKey = suspendIdempotencyKey,
                sourceManifestId = (Guid?)null,
                acceptedSelectionHash = manifest.CacheAcknowledgment!.Reference.Material!.AcceptedSelectionHash
            });
        if (suspendRequestHash != expectedRequestHash)
            throw new CoordinationException(
                "suspend_resume_manifest_invalid", StatusCodes.Status503ServiceUnavailable);

        var suspendOperation = new SessionSuspendResumeOperationRecord(
            operationId,
            identity,
            SessionSuspendResumeOperationKind.Suspend,
            phase,
            phaseVersion,
            operationFence,
            sourceManifestId,
            suspendIdempotencyKey,
            suspendRequestHash,
            progress,
            manifest,
            manifestHash,
            IsDuplicate: true);
        var saved = SessionSuspendResumeCoordinator.RequireCommittedProgress(suspendOperation);
        return new(
            manifest,
            manifestHash,
            SessionSuspendResumeCoordinator.CreatePlacementReleaseRequest(saved));
    }

    internal async Task<SessionSuspendResumeOperationRecord> BeginSessionSuspendAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionIdentity identity,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ValidateSessionSuspendResumeRequest(
            SessionSuspendResumeOperationKind.Suspend, idempotencyKey, sourceManifestId: null);
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
        if (run.ExecutionState == "completed" ||
            session.TurnState is "completed" or "failed" or "indeterminate")
            throw new CoordinationException(
                "suspend_resume_operation_conflict", StatusCodes.Status409Conflict);
        if (run.Fence <= 0)
            throw new CoordinationException(
                "suspend_resume_owner_fence_unavailable", StatusCodes.Status503ServiceUnavailable);

        var request = new
        {
            idempotencyKey,
            sourceManifestId = (Guid?)null,
            acceptedSelectionHash = run.SelectionHash
        };
        var requestHash = SessionSuspendResumePhaseMachine.HashRequest(
            SessionSuspendResumeOperationKind.Suspend, identity, request);
        var duplicate = await ReadExistingSessionSuspendResumeOperationRecordAsync(
            connection, transaction, actor, identity, SessionSuspendResumeOperationKind.Suspend,
            idempotencyKey, requestHash, run.Fence, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return duplicate with { IsDuplicate = true };
        }

        var operationId = Guid.NewGuid();
        var manifestId = Guid.NewGuid();
        var progress = SerializeSessionSuspendResumeProgress(SessionSuspendResumeProgress.Empty);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_executionOperations}
                (project_id, run_id, operation_id, session_id, reserved_manifest_id, actor_issuer, actor_subject,
                 operation_kind, idempotency_key, request_hash, result,
                 owner_execution_fence, operation_phase, phase_version, progress)
            VALUES
                (@project, @run, @operation, @session, @manifestId, @issuer, @subject,
                 'suspend', @key, @hash, @empty,
                 @fence, 'reserved', 0, @progress)
            """, connection, transaction))
        {
            AddRunScope(insert, identity.ProjectId, identity.RunId);
            insert.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            insert.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, manifestId);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, requestHash);
            insert.Parameters.AddWithValue("empty", NpgsqlDbType.Jsonb, "{}");
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, run.Fence);
            insert.Parameters.AddWithValue("progress", NpgsqlDbType.Jsonb, progress.GetRawText());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            operationId,
            identity,
            SessionSuspendResumeOperationKind.Suspend,
            SessionSuspendResumeOperationPhase.Reserved,
            0,
            run.Fence,
            manifestId,
            idempotencyKey,
            requestHash,
            progress,
            Manifest: null,
            ManifestHash: null,
            IsDuplicate: false);
    }

    internal async Task<SessionSuspendResumeOperationRecord> AdvanceSessionSuspendResumeAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionSuspendResumeOperationRecord operation,
        SessionSuspendResumeOperationPhase next,
        SessionSuspendResumeProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        SessionSuspendResumePhaseMachine.RequireValidAdvance(operation.Kind, operation.Phase, next);
        var serializedProgress = SerializeSessionSuspendResumeProgress(progress);
        RequireBoundedProgress(serializedProgress);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await RequireCurrentSessionSuspendOperationInTransactionAsync(
            connection, transaction, actor, selection, operation, cancellationToken).ConfigureAwait(false);

        var nextVersion = checked(operation.PhaseVersion + 1);
        await using var update = new NpgsqlCommand($"""
            UPDATE {_executionOperations}
            SET operation_phase = @nextPhase, phase_version = @nextVersion, progress = @progress
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation AND actor_issuer = @issuer AND actor_subject = @subject
                AND operation_kind = @kind AND idempotency_key = @key AND request_hash = @hash
                AND owner_execution_fence = @fence AND reserved_manifest_id = @manifestId
                AND operation_phase = @currentPhase AND phase_version = @currentVersion
            """, connection, transaction);
        AddRunScope(update, operation.Identity.ProjectId, operation.Identity.RunId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, operation.Identity.SessionId);
        update.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operation.OperationId);
        update.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        update.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        update.Parameters.AddWithValue(
            "kind", NpgsqlDbType.Varchar, SessionSuspendResumePhaseMachine.ToPersistenceValue(operation.Kind));
        update.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, operation.IdempotencyKey);
        update.Parameters.AddWithValue("hash", NpgsqlDbType.Char, operation.RequestHash);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, operation.OwnerExecutionFence);
        update.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, operation.ManifestId);
        update.Parameters.AddWithValue(
            "currentPhase", NpgsqlDbType.Varchar,
            SessionSuspendResumePhaseMachine.ToPersistenceValue(operation.Phase));
        update.Parameters.AddWithValue("currentVersion", NpgsqlDbType.Bigint, operation.PhaseVersion);
        update.Parameters.AddWithValue(
            "nextPhase", NpgsqlDbType.Varchar, SessionSuspendResumePhaseMachine.ToPersistenceValue(next));
        update.Parameters.AddWithValue("nextVersion", NpgsqlDbType.Bigint, nextVersion);
        update.Parameters.AddWithValue("progress", NpgsqlDbType.Jsonb, serializedProgress.GetRawText());
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException(
                "suspend_resume_operation_conflict", StatusCodes.Status409Conflict);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return operation with
        {
            Phase = next,
            PhaseVersion = nextVersion,
            Progress = serializedProgress,
            IsDuplicate = false
        };
    }

    internal async Task<SessionSuspendResumeOperationRecord> CommitSuspendedSessionManifestAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionSuspendResumeOperationRecord operation,
        SessionSuspendResumeManifest manifest,
        SessionSuspendResumeProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (operation.Kind != SessionSuspendResumeOperationKind.Suspend ||
            operation.Phase != SessionSuspendResumeOperationPhase.WorkspaceFlushed ||
            manifest.Identity != operation.Identity ||
            manifest.ManifestId != operation.ManifestId ||
            manifest.State != SessionSuspendResumeManifestState.Suspended ||
            manifest.CoreExecutionFence != operation.OwnerExecutionFence)
            throw new CoordinationException(
                "suspend_resume_manifest_binding_invalid", StatusCodes.Status409Conflict);
        try
        {
            manifest.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new CoordinationException(
                "suspend_resume_manifest_invalid", StatusCodes.Status503ServiceUnavailable, exception);
        }
        var serializedProgress = SerializeSessionSuspendResumeProgress(progress);
        RequireBoundedProgress(serializedProgress);
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        var manifestHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson)));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await RequireCurrentSessionSuspendOperationInTransactionAsync(
            connection, transaction, actor, selection, operation, cancellationToken).ConfigureAwait(false);

        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.session_consistency_manifests
                (project_id, run_id, session_id, operation_id, manifest_id, contract_version,
                 owner_execution_fence, claimed_execution_fence, manifest_state, manifest_hash, manifest)
            VALUES
                (@project, @run, @session, @operation, @manifestId, @contractVersion,
                 @ownerFence, @claimedFence, 'suspended', @hash, @manifest)
            """, connection, transaction))
        {
            AddRunScope(insert, operation.Identity.ProjectId, operation.Identity.RunId);
            insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, operation.Identity.SessionId);
            insert.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operation.OperationId);
            insert.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, operation.ManifestId);
            insert.Parameters.AddWithValue(
                "contractVersion", NpgsqlDbType.Integer, manifest.ContractVersion);
            insert.Parameters.AddWithValue("ownerFence", NpgsqlDbType.Bigint, operation.OwnerExecutionFence);
            insert.Parameters.AddWithValue("claimedFence", NpgsqlDbType.Bigint, operation.OwnerExecutionFence);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, manifestHash);
            insert.Parameters.AddWithValue("manifest", NpgsqlDbType.Jsonb, manifestJson);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var nextVersion = checked(operation.PhaseVersion + 1);
        await using (var update = new NpgsqlCommand($"""
            UPDATE {_executionOperations}
            SET operation_phase = 'manifest_committed', phase_version = @nextVersion,
                progress = @progress, result = @result
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation AND actor_issuer = @issuer AND actor_subject = @subject
                AND operation_kind = 'suspend' AND idempotency_key = @key AND request_hash = @requestHash
                AND owner_execution_fence = @fence AND reserved_manifest_id = @manifestId
                AND operation_phase = 'workspace_flushed' AND phase_version = @currentVersion
            """, connection, transaction))
        {
            AddRunScope(update, operation.Identity.ProjectId, operation.Identity.RunId);
            update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, operation.Identity.SessionId);
            update.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operation.OperationId);
            update.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            update.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            update.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, operation.IdempotencyKey);
            update.Parameters.AddWithValue("requestHash", NpgsqlDbType.Char, operation.RequestHash);
            update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, operation.OwnerExecutionFence);
            update.Parameters.AddWithValue("manifestId", NpgsqlDbType.Uuid, operation.ManifestId);
            update.Parameters.AddWithValue("currentVersion", NpgsqlDbType.Bigint, operation.PhaseVersion);
            update.Parameters.AddWithValue("nextVersion", NpgsqlDbType.Bigint, nextVersion);
            update.Parameters.AddWithValue("progress", NpgsqlDbType.Jsonb, serializedProgress.GetRawText());
            update.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new
            {
                state = "manifest_committed",
                operationId = operation.OperationId,
                manifestId = operation.ManifestId,
                manifestHash
            }, JsonOptions));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CoordinationException(
                    "suspend_resume_operation_conflict", StatusCodes.Status409Conflict);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return operation with
        {
            Phase = SessionSuspendResumeOperationPhase.ManifestCommitted,
            PhaseVersion = nextVersion,
            Progress = serializedProgress,
            Manifest = manifest,
            ManifestHash = manifestHash,
            IsDuplicate = false
        };
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

    private async Task<SessionSuspendResumeOperationRecord?> ReadExistingSessionSuspendResumeOperationRecordAsync(
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
        Guid operationId;
        Guid manifestId;
        string operationSessionId;
        string operationKind;
        string storedRequestHash;
        long ownerFence;
        long phaseVersion;
        SessionSuspendResumeOperationPhase phase;
        JsonElement progress;
        await using (var command = new NpgsqlCommand($"""
            SELECT operation_id, session_id, operation_kind, request_hash, operation_phase,
                phase_version, owner_execution_fence, reserved_manifest_id, progress::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run
                AND actor_issuer = @issuer AND actor_subject = @subject AND idempotency_key = @key
            FOR UPDATE
            """, connection, transaction))
        {
            AddRunScope(command, identity.ProjectId, identity.RunId);
            command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            operationId = reader.GetGuid(0);
            operationSessionId = reader.GetString(1);
            operationKind = reader.GetString(2);
            storedRequestHash = reader.GetString(3).TrimEnd();
            phase = SessionSuspendResumePhaseMachine.ParsePhase(reader.GetString(4));
            phaseVersion = reader.GetInt64(5);
            ownerFence = reader.GetInt64(6);
            manifestId = reader.IsDBNull(7) ? Guid.Empty : reader.GetGuid(7);
            progress = ReadJsonObject(reader.GetString(8), "suspend_resume_progress_invalid");
        }

        if (operationSessionId != identity.SessionId ||
            operationKind != SessionSuspendResumePhaseMachine.ToPersistenceValue(kind) ||
            !string.Equals(storedRequestHash, requestHash, StringComparison.Ordinal))
            throw new CoordinationException(
                "execution_operation_idempotency_conflict", StatusCodes.Status409Conflict);
        if (ownerFence != currentExecutionFence)
            throw new CoordinationException(
                "suspend_resume_operation_fence_stale", StatusCodes.Status409Conflict);

        var hasManifest = phase is SessionSuspendResumeOperationPhase.Interrupted
            or SessionSuspendResumeOperationPhase.ManifestCommitted
            or SessionSuspendResumeOperationPhase.PlacementReleased;
        SessionSuspendResumeManifest? manifest = null;
        string? manifestHash = null;
        await using (var command = new NpgsqlCommand($"""
            SELECT manifest_id, manifest_hash, manifest, owner_execution_fence, claimed_execution_fence
            FROM {_schema}.session_consistency_manifests
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation
            """, connection, transaction))
        {
            AddRunScope(command, identity.ProjectId, identity.RunId);
            command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!hasManifest || reader.GetGuid(0) != manifestId ||
                    reader.GetInt64(3) != ownerFence ||
                    (phase == SessionSuspendResumeOperationPhase.Interrupted && !reader.IsDBNull(4)) ||
                    (phase != SessionSuspendResumeOperationPhase.Interrupted &&
                     (reader.IsDBNull(4) || reader.GetInt64(4) != ownerFence)))
                    throw new CoordinationException(
                        "suspend_resume_manifest_fence_invalid", StatusCodes.Status503ServiceUnavailable);
                manifestHash = reader.GetString(1).TrimEnd();
                manifest = DeserializeAndValidateManifest(
                    reader.GetString(2), manifestHash, identity, manifestId);
                var expectedState = phase == SessionSuspendResumeOperationPhase.Interrupted
                    ? SessionSuspendResumeManifestState.Interrupted
                    : SessionSuspendResumeManifestState.Suspended;
                if (manifest.State != expectedState)
                    throw new CoordinationException(
                        "suspend_resume_manifest_invalid", StatusCodes.Status503ServiceUnavailable);
            }
            else if (hasManifest)
            {
                throw new CoordinationException(
                    "suspend_resume_manifest_unavailable", StatusCodes.Status503ServiceUnavailable);
            }
        }

        if (manifest is not null && phase == SessionSuspendResumeOperationPhase.Interrupted)
            RequireMatchingProgress(progress, manifestId, manifest);
        return new SessionSuspendResumeOperationRecord(
            operationId, identity, kind, phase, phaseVersion, ownerFence, manifestId,
            idempotencyKey, storedRequestHash, progress, manifest, manifestHash, IsDuplicate: true);
    }

    private async Task RequireCurrentSessionSuspendOperationInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionSuspendResumeOperationRecord operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var identity = operation.Identity;
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId,
            forUpdate: true, cancellationToken).ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireRunOwner(run, actor);
        RequireCurrentAuthorizedSelection(actor, selection, identity, run);
        RequireCurrentActiveSession(session, run);
        if (run.ExecutionState == "completed" ||
            session.TurnState is "completed" or "failed" or "indeterminate" ||
            run.Fence != operation.OwnerExecutionFence)
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

        await using var command = new NpgsqlCommand($"""
            SELECT actor_issuer, actor_subject, operation_kind, idempotency_key, request_hash,
                operation_phase, phase_version, owner_execution_fence, reserved_manifest_id, progress::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operation.OperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);
        var issuer = reader.GetString(0);
        var subject = reader.GetString(1);
        var kind = reader.GetString(2);
        var key = reader.GetString(3);
        var requestHash = reader.GetString(4).TrimEnd();
        var phase = SessionSuspendResumePhaseMachine.ParsePhase(reader.GetString(5));
        var phaseVersion = reader.GetInt64(6);
        var ownerFence = reader.GetInt64(7);
        var manifestId = reader.IsDBNull(8) ? Guid.Empty : reader.GetGuid(8);
        var storedProgress = ReadJsonObject(reader.GetString(9), "suspend_resume_progress_invalid");
        await reader.DisposeAsync().ConfigureAwait(false);

        var sourceManifestId = operation.Kind == SessionSuspendResumeOperationKind.Resume
            ? (Guid?)operation.ManifestId
            : null;
        var expectedRequestHash = SessionSuspendResumePhaseMachine.HashRequest(
            operation.Kind, identity,
            new
            {
                idempotencyKey = operation.IdempotencyKey,
                sourceManifestId,
                acceptedSelectionHash = run.SelectionHash
            });
        if (issuer != actor.Issuer || subject != actor.Subject ||
            kind != SessionSuspendResumePhaseMachine.ToPersistenceValue(operation.Kind) ||
            key != operation.IdempotencyKey || requestHash != operation.RequestHash ||
            requestHash != expectedRequestHash || phase != operation.Phase ||
            phaseVersion != operation.PhaseVersion || ownerFence != operation.OwnerExecutionFence ||
            manifestId != operation.ManifestId || !JsonElement.DeepEquals(storedProgress, operation.Progress))
            throw new CoordinationException(
                "suspend_resume_operation_stale", StatusCodes.Status409Conflict);
    }

    private static JsonElement SerializeSessionSuspendResumeProgress(SessionSuspendResumeProgress progress)
    {
        var value = JsonSerializer.SerializeToElement(progress, JsonOptions);
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Suspend/resume operation progress must be a JSON object.", nameof(progress));
        return value;
    }

    private static void RequireBoundedProgress(JsonElement progress)
    {
        if (progress.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(progress.GetRawText()) > 512 * 1024)
            throw new CoordinationException(
                "suspend_resume_progress_invalid", StatusCodes.Status400BadRequest);
    }

    private static SessionSuspendResumeProgress DeserializeSessionSuspendResumeProgress(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SessionSuspendResumeProgress>(json, JsonOptions)
                ?? throw new CoordinationException(
                    "suspend_resume_progress_invalid", StatusCodes.Status503ServiceUnavailable);
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "suspend_resume_progress_invalid", StatusCodes.Status503ServiceUnavailable, exception);
        }
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
