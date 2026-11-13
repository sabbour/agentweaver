using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Persistence.Postgres;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Owns accepted runs, session-tree state, messages, logical turns, and execution fencing in PostgreSQL.
/// </summary>
internal sealed partial class CoordinationOwnerStore
{
    private const string IngressConsumer = "orchestrator.addressed-message-ingress";
    private const int EventVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonSerializerOptions RuntimeRegistrationJsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly string _runs;
    private readonly string _sessions;
    private readonly string _messages;
    private readonly string _requests;
    private readonly string _notifications;
    private readonly string _decisions;
    private readonly string _executionOperations;
    private readonly PostgresOutbox _outbox;
    private readonly RuntimeObservationV2Store _runtimeObservationV2;
    private readonly TimeProvider _timeProvider;
    private readonly CoordinatorDecisionOwnerStore? _decisionStore;

    /// <summary>
    /// Creates the owner store for one validated schema and its optional decision-state collaborator.
    /// </summary>
    /// <exception cref="ArgumentException">The schema name is invalid or reserved.</exception>
    public CoordinationOwnerStore(
        NpgsqlDataSource dataSource,
        string schema,
        TimeProvider? timeProvider = null,
        CoordinatorDecisionOwnerStore? decisionStore = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ValidateSchema(schema);
        _dataSource = dataSource;
        _schema = $"\"{schema}\"";
        _runs = $"{_schema}.accepted_runs";
        _sessions = $"{_schema}.coordination_sessions";
        _messages = $"{_schema}.coordination_messages";
        _requests = $"{_schema}.coordination_requests";
        _notifications = $"{_schema}.parent_notifications";
        _decisions = $"{_schema}.coordinator_decisions";
        _executionOperations = $"{_schema}.coordination_execution_operations";
        _outbox = new PostgresOutbox(dataSource, schema);
        _runtimeObservationV2 = new RuntimeObservationV2Store(dataSource, schema);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _decisionStore = decisionStore;
    }

    /// <summary>
    /// Accepts the run's root session, preserving an existing identical binding and rejecting a conflicting one.
    /// </summary>
    /// <remarks>Admission and authority are rechecked in the transaction before its accepted run and root are committed.</remarks>
    public async Task<AcceptedRoot> AcceptRootAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        string sessionId,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<RuntimeRunAdmissionReceipt>>? readRunAdmission = null,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null)
    {
        ValidateAcceptRootInput(actor, selection, sessionId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var accepted = await AcceptRootInTransactionAsync(
            connection, transaction, actor, selection, sessionId, cancellationToken,
            readRunAdmission, revalidateCurrentAuthority).ConfigureAwait(false);
        if (revalidateCurrentAuthority is not null)
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return accepted;
    }

    /// <summary>
    /// Accepts a root session using a caller-owned transaction so other owner changes can commit atomically with it.
    /// </summary>
    /// <exception cref="CoordinationException">The run is already bound to a different selection, actor, tenant, or root.</exception>
    internal async Task<AcceptedRoot> AcceptRootInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        string sessionId,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<RuntimeRunAdmissionReceipt>>? readRunAdmission = null,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null,
        bool requireNewRoot = false,
        bool reuseExistingRoot = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        ValidateAcceptRootInput(actor, selection, sessionId);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        var effectiveSelection = selection.Selection;
        var snapshot = effectiveSelection.Snapshot.GetRawText();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
        await PrepareRunAdmissionInTransactionAsync(
            connection, transaction, selection, readRunAdmission, revalidateCurrentAuthority, cancellationToken)
            .ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_runs}
                (project_id, run_id, accepted_selection, accepted_selection_hash,
                 accepted_by_issuer, accepted_by_subject, tenant_id)
            VALUES (@project, @run, @selection, @hash, @issuer, @subject, @tenant)
            ON CONFLICT (project_id, run_id) DO NOTHING
            """, connection, transaction))
        {
            AddRunScope(insert, effectiveSelection.ProjectId, effectiveSelection.RunId);
            insert.Parameters.AddWithValue("selection", NpgsqlDbType.Jsonb, snapshot);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, hash);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, selection.Authorization.TenantId);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
            if (requireNewRoot && inserted != 1)
                throw new CoordinationException(
                    "p1_invocation_binding_missing", StatusCodes.Status409Conflict);
        }

        await using (var bindTenant = new NpgsqlCommand($"""
            UPDATE {_runs}
            SET tenant_id = @tenant
            WHERE project_id = @project AND run_id = @run
              AND tenant_id IS NULL
              AND accepted_selection_hash = @hash
              AND accepted_by_issuer = @issuer
              AND accepted_by_subject = @subject
            """, connection, transaction))
        {
            AddRunScope(bindTenant, effectiveSelection.ProjectId, effectiveSelection.RunId);
            bindTenant.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, selection.Authorization.TenantId);
            bindTenant.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, hash);
            bindTenant.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            bindTenant.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            await bindTenant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var acceptedRun = await ReadAcceptedRunAsync(
            connection,
            transaction,
            effectiveSelection.ProjectId,
            effectiveSelection.RunId,
            forUpdate: true,
            cancellationToken)
            .ConfigureAwait(false);
        if (acceptedRun.SelectionHash != hash || acceptedRun.AcceptedIssuer != actor.Issuer ||
            acceptedRun.AcceptedSubject != actor.Subject ||
            acceptedRun.TenantId != selection.Authorization.TenantId)
            throw new CoordinationException("accepted_run_conflict", StatusCodes.Status409Conflict);

        var root = await ReadRootSessionAsync(
            connection,
            transaction,
            effectiveSelection.ProjectId,
            effectiveSelection.RunId,
            forUpdate: true,
            cancellationToken)
            .ConfigureAwait(false);
        var rootCreated = root is null;
        if (rootCreated)
        {
            await using var insertSession = new NpgsqlCommand($"""
                INSERT INTO {_sessions}
                    (project_id, run_id, session_id, parent_session_id, root_session_id, node_kind,
                     writer_issuer, writer_subject, execution_fence)
                VALUES (@project, @run, @session, NULL, @session, 'coordinator', @issuer, @subject, @fence)
                """, connection, transaction);
            AddRunScope(insertSession, effectiveSelection.ProjectId, effectiveSelection.RunId);
            insertSession.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, sessionId);
            insertSession.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insertSession.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insertSession.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, acceptedRun.Fence);
            await insertSession.ExecuteNonQueryAsync(cancellationToken);
            root = await ReadRootSessionAsync(
                connection,
                transaction,
                effectiveSelection.ProjectId,
                effectiveSelection.RunId,
                forUpdate: false,
                cancellationToken)
                .ConfigureAwait(false);
        }
        if (root is null || !reuseExistingRoot && root.SessionId != sessionId ||
            root.WriterIssuer != actor.Issuer || root.WriterSubject != actor.Subject)
            throw new CoordinationException("root_session_conflict", StatusCodes.Status409Conflict);

        if (rootCreated)
        {
            var coverage = RuntimeObservationV2Store.CoverageStartedFact(
                effectiveSelection.ProjectId, effectiveSelection.RunId, root.SessionId,
                acceptedRun.Fence, hash, _timeProvider.GetUtcNow());
            await _runtimeObservationV2.AppendAsync(
                connection, transaction, effectiveSelection.ProjectId, effectiveSelection.RunId,
                coverage, cancellationToken).ConfigureAwait(false);
        }

        return new AcceptedRoot(
            effectiveSelection.ProjectId,
            effectiveSelection.RunId,
            root.SessionId,
            acceptedRun.Fence,
            acceptedRun.StateVersion,
            acceptedRun.LogicalTurnOrdinal,
            acceptedRun.ExecutionState);
    }

    /// <summary>
    /// Reads and locks the accepted root and latest decision envelope for a prerequisite claim.
    /// </summary>
    /// <returns>The current root, owner actor, tenant, accepted selection, hash, and decision version.</returns>
    /// <exception cref="CoordinationException">The root binding, accepted selection, or persisted decision envelope is missing, stale, or corrupt.</exception>
    internal async Task<(
        AcceptedRoot Root,
        CoordinationActor Actor,
        string TenantId,
        EffectiveRunSelection Selection,
        string AcceptedSelectionHash,
        long DecisionStateVersion)> ReadPrerequisiteRootBindingInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        string rootSessionId,
        string expectedSelectionHash,
        long expectedFence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        CoordinationIdentity.ValidateIdentity(projectId, nameof(projectId));
        CoordinationIdentity.ValidateIdentity(runId, nameof(runId));
        CoordinationIdentity.ValidateIdentity(rootSessionId, nameof(rootSessionId));
        if (string.IsNullOrWhiteSpace(expectedSelectionHash) ||
            expectedSelectionHash.Length != 64 ||
            expectedFence <= 0)
            throw new CoordinationException(
                "backlog_prerequisite_binding_invalid", StatusCodes.Status409Conflict);

        await using var command = new NpgsqlCommand($"""
            SELECT r.accepted_selection::text, r.accepted_selection_hash,
                   r.accepted_by_issuer, r.accepted_by_subject, r.tenant_id,
                   r.execution_fence, r.logical_turn_ordinal, r.execution_state, r.state_version,
                   s.session_id, s.root_session_id, s.node_kind, s.writer_issuer, s.writer_subject,
                   s.execution_fence, s.lifecycle_state,
                   latest.state_version, latest.decision::text
            FROM {_runs} AS r
            INNER JOIN {_sessions} AS s
              ON s.project_id = r.project_id AND s.run_id = r.run_id
            LEFT JOIN LATERAL (
                SELECT d.state_version, d.decision
                FROM {_decisions} AS d
                WHERE d.project_id = r.project_id AND d.run_id = r.run_id
                  AND d.session_id = s.session_id
                ORDER BY d.state_version DESC
                LIMIT 1
            ) AS latest ON true
            WHERE r.project_id = @project AND r.run_id = @run
              AND s.session_id = @root_session AND s.parent_session_id IS NULL
            FOR UPDATE OF r, s
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("root_session", NpgsqlDbType.Varchar, rootSessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "backlog_prerequisite_binding_unavailable", StatusCodes.Status409Conflict);

        var selectionHash = reader.GetString(1).TrimEnd();
        var issuer = reader.GetString(2);
        var subject = reader.GetString(3);
        var tenantId = reader.IsDBNull(4) ? null : reader.GetString(4);
        var runFence = reader.GetInt64(5);
        var logicalTurnOrdinal = reader.GetInt64(6);
        var executionState = reader.GetString(7);
        var stateVersion = reader.GetInt64(8);
        var sessionId = reader.GetString(9);
        var persistedRootId = reader.GetString(10);
        var nodeKind = reader.GetString(11);
        var writerIssuer = reader.GetString(12);
        var writerSubject = reader.GetString(13);
        var sessionFence = reader.GetInt64(14);
        var lifecycleState = reader.GetString(15);
        var selectionJson = reader.GetString(0);
        var decisionStateVersion = reader.IsDBNull(16) ? 0 : reader.GetInt64(16);
        var decisionPayload = reader.IsDBNull(17) ? null : reader.GetString(17);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            tenantId is null ||
            selectionHash.Length != 64 ||
            !string.Equals(selectionHash, expectedSelectionHash, StringComparison.Ordinal) ||
            runFence != expectedFence ||
            sessionFence != expectedFence ||
            stateVersion < 1 ||
            logicalTurnOrdinal < 0 ||
            sessionId != rootSessionId ||
            persistedRootId != rootSessionId ||
            nodeKind != "coordinator" ||
            issuer != writerIssuer ||
            subject != writerSubject ||
            lifecycleState == "cancelled")
            throw new CoordinationException(
                "backlog_prerequisite_binding_stale", StatusCodes.Status409Conflict);

        if (decisionStateVersion < 1 || decisionPayload is null)
            throw new CoordinationException(
                "backlog_prerequisite_binding_unavailable", StatusCodes.Status409Conflict);
        CoordinatorDecisionStateEnvelope? envelope;
        try
        {
            using var decisionDocument = JsonDocument.Parse(decisionPayload);
            envelope = decisionDocument.RootElement.TryGetProperty("envelope", out var persistedEnvelope)
                ? JsonSerializer.Deserialize<CoordinatorDecisionStateEnvelope>(
                    persistedEnvelope.GetRawText(), JsonOptions)
                : null;
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "backlog_prerequisite_binding_corrupt",
                StatusCodes.Status503ServiceUnavailable,
                exception);
        }
        if (envelope is null ||
            envelope.Version != CoordinatorDecisionStateEnvelope.CurrentVersion ||
            envelope.ProjectId != projectId ||
            envelope.RunId != runId ||
            envelope.RootSessionId != rootSessionId ||
            envelope.Issuer != issuer ||
            envelope.Subject != subject ||
            envelope.TenantId != tenantId ||
            envelope.AcceptedSelectionHash != selectionHash ||
            envelope.Fence != expectedFence ||
            envelope.ProjectRevision < 1 ||
            envelope.ProjectConfigurationRevision < 1 ||
            envelope.PlatformRuntimeRevision < 1 ||
            string.IsNullOrWhiteSpace(envelope.ContextRevision))
            throw new CoordinationException(
                "backlog_prerequisite_binding_corrupt", StatusCodes.Status503ServiceUnavailable);

        JsonElement snapshot;
        try
        {
            using var selectionDocument = JsonDocument.Parse(selectionJson);
            snapshot = selectionDocument.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "backlog_prerequisite_binding_corrupt",
                StatusCodes.Status503ServiceUnavailable,
                exception);
        }
        var selection = new EffectiveRunSelection(
            projectId,
            runId,
            envelope.ProjectRevision,
            envelope.ProjectConfigurationRevision,
            envelope.PlatformRuntimeRevision,
            envelope.ContextRevision,
            snapshot);
        ValidateSelection(selection);

        var actor = new CoordinationActor(issuer, subject);
        var root = new AcceptedRoot(
            projectId,
            runId,
            rootSessionId,
            runFence,
            stateVersion,
            logicalTurnOrdinal,
            executionState);
        return (root, actor, tenantId, selection, selectionHash, decisionStateVersion);
    }

    /// <summary>
    /// Checks that Projects granted this actor permission to accept the exact project and run selection.
    /// </summary>
    /// <exception cref="CoordinationException">The authorization context or accepted selection is invalid.</exception>
    private static void ValidateAcceptRootInput(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        string sessionId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        var effectiveSelection = selection.Selection;
        if (selection.Authorization.ContractVersion != 1 ||
            selection.Authorization.Issuer != actor.Issuer ||
            selection.Authorization.ActorId != actor.Subject ||
            string.IsNullOrWhiteSpace(selection.Authorization.TenantId) ||
            selection.Authorization.BoundProjectId != effectiveSelection.ProjectId ||
            selection.Authorization.BoundRunId != effectiveSelection.RunId ||
            selection.Authorization.MembershipRevision < 1 ||
            selection.Authorization.EffectiveAuthority.IsDefault ||
            !selection.Authorization.EffectiveAuthority.Any(authority =>
                authority.ResourceType == "project" &&
                authority.ResourceId == effectiveSelection.ProjectId &&
                !authority.Permissions.IsDefault &&
                authority.Permissions.Any(permission =>
                    permission.Permission == "acceptRunSelection" &&
                    permission.RoleRevision > 0)))
            throw new CoordinationException(
                "run_selection_permission_denied", StatusCodes.Status403Forbidden);
        CoordinationIdentity.ValidateIdentity(sessionId, nameof(sessionId));
        ValidateSelection(effectiveSelection);
        _ = RequiresRunAdmission(effectiveSelection.Snapshot);
    }

    /// <summary>
    /// Registers an already-created child session under its parent, subject to owner and capacity checks.
    /// </summary>
    public async Task<RegisteredChild> RegisterChildAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        string childSessionId,
        int maxChildren,
        int maxConcurrentChildren,
        CancellationToken cancellationToken,
        ConfirmedWorkPlanItemAssociation? workPlanItemAssociation = null,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null)
    {
        var result = await RegisterChildCoreAsync(
            actor, parent, childSessionId, CoordinationSessionKind.ChildWork, null,
            workPlanItemAssociation, maxChildren, maxConcurrentChildren, cancellationToken,
            revalidateCurrentAuthority).ConfigureAwait(false);
        return result.Registered;
    }

    /// <summary>
    /// Registers a child and persists its idempotent spawn request for later dispatch.
    /// </summary>
    /// <exception cref="CoordinationException">The request is invalid, capacity is exhausted, or the spawn cannot be persisted.</exception>
    public async Task<SpawnedSession> SpawnSessionAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        SpawnSessionRequest request,
        int maxChildren,
        int maxConcurrentChildren,
        CancellationToken cancellationToken,
        ConfirmedWorkPlanItemAssociation? workPlanItemAssociation = null,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null,
        Func<NpgsqlConnection, NpgsqlTransaction, SpawnedSession, CancellationToken, Task>?
            persistSpawnedInTransaction = null)
    {
        ValidateSpawnRequest(request);
        var result = await RegisterChildCoreAsync(
            actor, parent, request.SessionId, request.Kind, request,
            workPlanItemAssociation, maxChildren, maxConcurrentChildren, cancellationToken,
            revalidateCurrentAuthority,
            persistSpawnedInTransaction).ConfigureAwait(false);
        return result.Spawned
            ?? throw new CoordinationException("session_spawn_unavailable", StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Validates an Events fork result against a pending, selection-bound owner command.
    /// </summary>
    /// <remarks>The receipt binds the fork event and cursor, target, source, actor, idempotency key, and accepted selection.</remarks>
    /// <exception cref="CoordinationException">The pending fork is absent, stale, inconsistent, or no longer admissible.</exception>
    public async Task<SessionForkAdmissionReceipt> ValidateSessionForkAdmissionAsync(
        CoordinationActor actor,
        SessionIdentity source,
        SessionForkRequest request,
        string acceptedSelectionHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        ValidateAcceptedSelectionHash(acceptedSelectionHash);
        CoordinationIdentity.ValidateIdentity(request.TargetSessionId, nameof(request.TargetSessionId));
        CoordinationIdentity.ValidateIdentity(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (request.SourceEventId == Guid.Empty ||
            request.IdempotencyKey.Length > 128 ||
            request.IdempotencyKey.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(request.SourceCursor) ||
            request.SourceCursor.Length > 2048 ||
            request.SourceCursor.Contains('\0') ||
            request.TargetSessionId == source.SessionId)
            throw new CoordinationException("session_fork_invalid", StatusCodes.Status400BadRequest);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, source.ProjectId, source.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var sourceSession = await ReadSessionAsync(
            connection, transaction, source, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(sourceSession, actor);
        RequireCurrentActiveSession(sourceSession, run);

        Guid commandId;
        string targetSessionId;
        string commandHash;
        long executionFence;
        string? reservedSelectionHash;
        CoordinationSessionForkResult result;
        await using (var command = new NpgsqlCommand($"""
            SELECT command_id, requested_target_session_id, command_hash, execution_fence,
                   fork_accepted_selection_hash, result::text
            FROM {_schema}.coordination_tree_commands
            WHERE project_id = @project AND run_id = @run AND source_session_id = @source
                AND actor_issuer = @issuer AND actor_subject = @subject
                AND idempotency_key = @key AND command_kind = 'fork'
            FOR UPDATE
            """, connection, transaction))
        {
            AddRunScope(command, source.ProjectId, source.RunId);
            command.Parameters.AddWithValue("source", NpgsqlDbType.Varchar, source.SessionId);
            command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, request.IdempotencyKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException(
                    "session_fork_admission_denied", StatusCodes.Status409Conflict);
            commandId = reader.GetGuid(0);
            targetSessionId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            commandHash = reader.GetString(2).TrimEnd();
            executionFence = reader.GetInt64(3);
            reservedSelectionHash = reader.IsDBNull(4) ? null : reader.GetString(4).TrimEnd();
            try
            {
                result = JsonSerializer.Deserialize<CoordinationSessionForkResult>(
                    reader.GetString(5), JsonOptions) ?? throw new JsonException();
            }
            catch (JsonException exception)
            {
                throw new CoordinationException(
                    "session_fork_operation_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
            }
        }

        if (result.CommandId != commandId ||
            result.Source != source ||
            result.TargetSessionId != targetSessionId ||
            result.ExecutionFence != executionFence ||
            result.RegistrationState != CoordinationForkRegistrationState.RegistrationPending ||
            reservedSelectionHash is null ||
            !string.Equals(reservedSelectionHash, acceptedSelectionHash, StringComparison.Ordinal))
            throw new CoordinationException("session_fork_admission_denied", StatusCodes.Status409Conflict);

        var coordinatedRequest = new CoordinationSessionForkRequest(
            executionFence,
            request.IdempotencyKey,
            request.TargetSessionId,
            request.SourceEventId,
            request.SourceCursor,
            result.Kind);
        ValidateForkRequest(coordinatedRequest);
        if (!string.Equals(
                commandHash,
                HashForkCommand(source, coordinatedRequest),
                StringComparison.Ordinal))
            throw new CoordinationException("idempotency_conflict", StatusCodes.Status409Conflict);

        var unavailableCode = GetForkUnavailableCode(
            run, sourceSession, coordinatedRequest, acceptedSelectionHash);
        if (unavailableCode is not null)
            throw new CoordinationException(unavailableCode, StatusCodes.Status409Conflict);

        var receipt = new SessionForkAdmissionReceipt(
            commandId,
            source,
            targetSessionId,
            request.SourceEventId,
            request.SourceCursor,
            request.IdempotencyKey,
            executionFence,
            actor.Issuer,
            actor.Subject,
            reservedSelectionHash);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    /// <summary>
    /// Creates an idempotent pending fork command after checking the source session, target reservation, and capacity.
    /// </summary>
    /// <remarks>The Events owner completes source-event creation separately; this transaction reserves the target and records the pending intent.</remarks>
    public async Task<CoordinationSessionForkResult> PrepareSessionForkAsync(
        CoordinationActor actor,
        SessionIdentity source,
        CoordinationSessionForkRequest request,
        string acceptedSelectionHash,
        int maxChildren,
        int maxConcurrentChildren,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null,
        Func<NpgsqlConnection, NpgsqlTransaction, SpawnedSession, CancellationToken, Task>?
            persistSpawnedInTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateForkRequest(request);
        ValidateAcceptedSelectionHash(acceptedSelectionHash);
        ValidateChildLimits(maxChildren, maxConcurrentChildren);
        var commandHash = HashForkCommand(source, request);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, source.ProjectId, source.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var sourceSession = await ReadSessionAsync(
            connection, transaction, source, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(sourceSession, actor);
        if (revalidateCurrentAuthority is not null)
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);

        var existing = await ReadExistingForkCommandAsync(
            connection, transaction, source, actor, request.IdempotencyKey, commandHash, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.RegistrationState != CoordinationForkRegistrationState.RegistrationPending)
            {
                if (revalidateCurrentAuthority is not null)
                    await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing with { IsDuplicate = true };
            }

            var unavailableCode = GetForkUnavailableCode(
                run, sourceSession, request, acceptedSelectionHash);
            if (unavailableCode is not null)
            {
                var unregistered = await FinalizeUnregisteredForkAsync(
                    connection,
                    transaction,
                    source,
                    actor,
                    request,
                    acceptedSelectionHash,
                    existing,
                    lineage: null,
                    unavailableCode,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return unregistered with { IsDuplicate = true };
            }

            if (revalidateCurrentAuthority is not null)
                await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing with { IsDuplicate = true };
        }

        var initialUnavailableCode = GetForkUnavailableCode(
            run, sourceSession, request, acceptedSelectionHash);
        if (initialUnavailableCode is not null)
            throw new CoordinationException(initialUnavailableCode, StatusCodes.Status409Conflict);
        if (await FindSessionAsync(
                connection,
                transaction,
                source.ProjectId,
                source.RunId,
                request.TargetSessionId,
                forUpdate: true,
                cancellationToken).ConfigureAwait(false) is not null ||
            await IsForkTargetReservedAsync(
                connection, transaction, source.ProjectId, source.RunId,
                request.TargetSessionId, cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("session_fork_target_conflict", StatusCodes.Status409Conflict);

        var capacityError = await ReadForkCapacityErrorAsync(
            connection,
            transaction,
            source.ProjectId,
            source.RunId,
            maxChildren,
            maxConcurrentChildren,
            pendingForksToAdd: 1,
            cancellationToken).ConfigureAwait(false);
        if (capacityError is not null)
            throw new CoordinationException(capacityError, StatusCodes.Status409Conflict);

        // Reserve capacity and target identity before Events performs the separate fork operation.
        var pending = new CoordinationSessionForkResult(
            Guid.NewGuid(),
            source,
            request.TargetSessionId,
            request.Kind,
            request.ExecutionFence,
            CoordinationForkRegistrationState.RegistrationPending,
            null,
            null,
            null,
            null,
            IsDuplicate: false);
        var pendingJson = JsonSerializer.SerializeToElement(pending, JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.coordination_tree_commands
                (command_id, project_id, run_id, source_session_id, target_session_id,
                 requested_target_session_id, actor_issuer, actor_subject, execution_fence,
                 command_kind, idempotency_key, command_hash, result,
                 fork_accepted_selection_hash)
            VALUES (@command, @project, @run, @source, NULL, @requestedTarget,
                @issuer, @subject, @fence, 'fork', @key, @hash, @result, @selectionHash)
            """, connection, transaction))
        {
            AddRunScope(insert, source.ProjectId, source.RunId);
            insert.Parameters.AddWithValue("command", NpgsqlDbType.Uuid, pending.CommandId);
            insert.Parameters.AddWithValue("source", NpgsqlDbType.Varchar, source.SessionId);
            insert.Parameters.AddWithValue(
                "requestedTarget", NpgsqlDbType.Varchar, request.TargetSessionId);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, request.ExecutionFence);
            insert.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, request.IdempotencyKey);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, commandHash);
            insert.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, pendingJson.GetRawText());
            insert.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, acceptedSelectionHash);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (revalidateCurrentAuthority is not null)
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return pending;
    }

    /// <summary>
    /// Finalizes a pending fork as unregistered after its admission step fails.
    /// </summary>
    public async Task<CoordinationSessionForkResult> FinalizeSessionForkAdmissionFailureAsync(
        CoordinationActor actor,
        SessionIdentity source,
        CoordinationSessionForkRequest request,
        string acceptedSelectionHash,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateForkRequest(request);
        ValidateAcceptedSelectionHash(acceptedSelectionHash);
        var commandHash = HashForkCommand(source, request);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        _ = await ReadAcceptedRunAsync(
            connection, transaction, source.ProjectId, source.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var sourceSession = await ReadSessionAsync(
            connection, transaction, source, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(sourceSession, actor);
        var existing = await ReadExistingForkCommandAsync(
            connection, transaction, source, actor, request.IdempotencyKey, commandHash, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CoordinationException(
                "session_fork_operation_unavailable", StatusCodes.Status409Conflict);
        if (existing.RegistrationState != CoordinationForkRegistrationState.RegistrationPending)
        {
            if (revalidateCurrentAuthority is not null)
                await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing with { IsDuplicate = true };
        }

        await using (var selectionHashCommand = new NpgsqlCommand($"""
            SELECT fork_accepted_selection_hash
            FROM {_schema}.coordination_tree_commands
            WHERE project_id = @project AND run_id = @run AND command_id = @command
            """, connection, transaction))
        {
            AddRunScope(selectionHashCommand, source.ProjectId, source.RunId);
            selectionHashCommand.Parameters.AddWithValue("command", NpgsqlDbType.Uuid, existing.CommandId);
            var storedSelectionHash =
                (string?)await selectionHashCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (storedSelectionHash is null ||
                !string.Equals(storedSelectionHash.TrimEnd(), acceptedSelectionHash, StringComparison.Ordinal))
                throw new CoordinationException(
                    "session_fork_operation_unavailable", StatusCodes.Status409Conflict);
        }

        var unregistered = await FinalizeUnregisteredForkAsync(
            connection,
            transaction,
            source,
            actor,
            request,
            acceptedSelectionHash,
            existing,
            lineage: null,
            "session_fork_admission_denied",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return unregistered;
    }

    /// <summary>
    /// Completes a pending fork by recording the Events result and registering the target when current authority permits.
    /// </summary>
    /// <remarks>Duplicate command results are returned as duplicates; stale authority or selection prevents registration.</remarks>
    public async Task<CoordinationSessionForkResult> CompleteSessionForkAsync(
        CoordinationActor actor,
        SessionIdentity source,
        CoordinationSessionForkRequest request,
        string acceptedSelectionHash,
        string currentSelectionHash,
        bool selectionIsCurrent,
        SessionForkResult eventsFork,
        int maxChildren,
        int maxConcurrentChildren,
        bool isDuplicate,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateForkRequest(request);
        ValidateAcceptedSelectionHash(acceptedSelectionHash);
        ValidateAcceptedSelectionHash(currentSelectionHash);
        ValidateEventsForkResult(source, request, eventsFork);
        ValidateChildLimits(maxChildren, maxConcurrentChildren);
        var commandHash = HashForkCommand(source, request);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, source.ProjectId, source.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var sourceSession = await ReadSessionAsync(
            connection, transaction, source, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(sourceSession, actor);

        var existing = await ReadExistingForkCommandAsync(
            connection, transaction, source, actor, request.IdempotencyKey, commandHash, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CoordinationException(
                "session_fork_operation_unavailable", StatusCodes.Status409Conflict);
        CoordinationException? authorityError = null;
        if (revalidateCurrentAuthority is not null)
        {
            try
            {
                await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
            }
            catch (CoordinationException exception) when (
                exception.StatusCode is StatusCodes.Status403Forbidden or StatusCodes.Status409Conflict)
            {
                authorityError = exception;
            }
        }
        if (existing.RegistrationState != CoordinationForkRegistrationState.RegistrationPending)
        {
            if (authorityError is not null)
                throw authorityError;
            if (existing.Lineage is not null && existing.Lineage != eventsFork.Lineage)
                throw new CoordinationException(
                    "session_fork_contract_invalid", StatusCodes.Status502BadGateway);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing with { IsDuplicate = true };
        }

        var unavailableCode = authorityError?.Code ??
            (!selectionIsCurrent ||
            !string.Equals(acceptedSelectionHash, currentSelectionHash, StringComparison.Ordinal)
                ? "coordinator_selection_stale"
                : GetForkUnavailableCode(run, sourceSession, request, acceptedSelectionHash));
        if (unavailableCode is null)
        {
            if (await FindSessionAsync(
                    connection,
                    transaction,
                    source.ProjectId,
                    source.RunId,
                    request.TargetSessionId,
                    forUpdate: true,
                    cancellationToken).ConfigureAwait(false) is not null)
                unavailableCode = "session_fork_target_conflict";
        }
        if (unavailableCode is null)
            unavailableCode = await ReadForkCapacityErrorAsync(
                connection,
                transaction,
                source.ProjectId,
                source.RunId,
                maxChildren,
                maxConcurrentChildren,
                pendingForksToAdd: 0,
                cancellationToken).ConfigureAwait(false);

        if (unavailableCode is not null)
        {
            var unregistered = await FinalizeUnregisteredForkAsync(
                connection,
                transaction,
                source,
                actor,
                request,
                acceptedSelectionHash,
                existing,
                eventsFork.Lineage,
                unavailableCode,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return unregistered with { IsDuplicate = isDuplicate };
        }

        var pendingRequestId = Guid.NewGuid().ToString("N");
        await using (var savepoint = new NpgsqlCommand(
            "SAVEPOINT session_fork_registration", connection, transaction))
            await savepoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using (var insertSession = new NpgsqlCommand($"""
            INSERT INTO {_sessions}
                (project_id, run_id, session_id, parent_session_id, root_session_id, node_kind,
                 writer_issuer, writer_subject, execution_fence)
            VALUES (@project, @run, @target, @source, @root, @kind, @issuer, @subject, @fence)
            """, connection, transaction))
        {
            AddRunScope(insertSession, source.ProjectId, source.RunId);
            insertSession.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, request.TargetSessionId);
            insertSession.Parameters.AddWithValue("source", NpgsqlDbType.Varchar, source.SessionId);
            insertSession.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, sourceSession.RootSessionId);
            insertSession.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, ToDatabaseSessionKind(request.Kind));
            insertSession.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insertSession.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insertSession.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, request.ExecutionFence);
            await insertSession.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var insertRequest = new NpgsqlCommand($"""
            INSERT INTO {_requests}
                (project_id, run_id, request_id, sender_session_id, recipient_session_id)
            VALUES (@project, @run, @request, @sender, @recipient)
            """, connection, transaction))
        {
            AddRunScope(insertRequest, source.ProjectId, source.RunId);
            insertRequest.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, pendingRequestId);
            insertRequest.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, source.SessionId);
            insertRequest.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, request.TargetSessionId);
            await insertRequest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var targetIdentity = new SessionIdentity(
            source.ProjectId, source.RunId, request.TargetSessionId);
        var targetSession = await ReadSessionAsync(
            connection, transaction, targetIdentity, forUpdate: false, cancellationToken).ConfigureAwait(false);
        var node = ToSessionTreeNode(targetSession, targetIdentity);
        var result = existing with
        {
            RegistrationState = CoordinationForkRegistrationState.Registered,
            Node = node,
            PendingRequestId = pendingRequestId,
            Lineage = eventsFork.Lineage,
            UnavailableCode = null,
            IsDuplicate = false
        };
        await UpdateForkCommandAsync(
            connection,
            transaction,
            source,
            result,
            request.TargetSessionId,
            cancellationToken).ConfigureAwait(false);
        await EnqueueForkOutboxAsync(
            connection, transaction, source, actor, request, acceptedSelectionHash,
            result, "orchestrator.session.forked", cancellationToken).ConfigureAwait(false);

        CoordinationException? finalAuthorityError = null;
        if (revalidateCurrentAuthority is not null)
        {
            try
            {
                await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
            }
            catch (CoordinationException exception) when (
                exception.StatusCode is StatusCodes.Status403Forbidden or StatusCodes.Status409Conflict)
            {
                finalAuthorityError = exception;
            }
        }
        if (finalAuthorityError is not null)
        {
            await using (var rollback = new NpgsqlCommand(
                "ROLLBACK TO SAVEPOINT session_fork_registration", connection, transaction))
                await rollback.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var unregistered = await FinalizeUnregisteredForkAsync(
                connection,
                transaction,
                source,
                actor,
                request,
                acceptedSelectionHash,
                existing,
                eventsFork.Lineage,
                finalAuthorityError.Code,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return unregistered with { IsDuplicate = isDuplicate };
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result with { IsDuplicate = isDuplicate };
    }

    /// <summary>
    /// Performs the shared child registration transaction for direct registration and spawn requests.
    /// </summary>
    /// <remarks>It verifies the parent writer, run fence, capacity, optional work-plan association, and idempotency before inserting a child.</remarks>
    private async Task<SessionRegistrationResult> RegisterChildCoreAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        string childSessionId,
        CoordinationSessionKind kind,
        SpawnSessionRequest? spawnRequest,
        ConfirmedWorkPlanItemAssociation? workPlanItemAssociation,
        int maxChildren,
        int maxConcurrentChildren,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null,
        Func<NpgsqlConnection, NpgsqlTransaction, SpawnedSession, CancellationToken, Task>?
            persistSpawnedInTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        CoordinationIdentity.ValidateIdentity(childSessionId, nameof(childSessionId));
        if (maxChildren is < 0 or > 100 ||
            maxConcurrentChildren is < 1 or > 32)
            throw new CoordinationException("run_child_limit_invalid", StatusCodes.Status502BadGateway);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, parent.ProjectId, parent.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var parentSession = await ReadSessionAsync(
            connection, transaction, parent, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(parentSession, actor);
        RequireCurrentActiveSession(parentSession, run);
        await RequireNoCheckpointRecoveryHoldAsync(
            connection, transaction, parent.ProjectId, parent.RunId, cancellationToken).ConfigureAwait(false);
        if (parentSession.LifecycleState != "active")
            throw new CoordinationException("parent_session_unavailable", StatusCodes.Status409Conflict);
        if (spawnRequest is not null &&
            kind == CoordinationSessionKind.ChildRun &&
            parentSession.NodeKind != "operator_chat")
            throw new CoordinationException(
                "child_run_spawn_requires_operator_chat", StatusCodes.Status403Forbidden);

        var requestedWorkPlanItemId = spawnRequest is null
            ? workPlanItemAssociation?.WorkPlanItemId
            : spawnRequest.WorkPlanItemId;
        // A caller-supplied item ID is not authority; verify it against the current root decision.
        if (requestedWorkPlanItemId is { } workPlanItemId)
        {
            CoordinationIdentity.ValidateIdentity(workPlanItemId, nameof(workPlanItemId));
            if (workPlanItemAssociation is null ||
                workPlanItemAssociation.WorkPlanItemId != workPlanItemId ||
                workPlanItemAssociation.DecisionStateVersion < 1 ||
                workPlanItemAssociation.SelectionHash != run.SelectionHash)
                throw new CoordinationException(
                    "session_work_plan_item_unavailable", StatusCodes.Status409Conflict);

            var rootIdentity = new SessionIdentity(
                parent.ProjectId, parent.RunId, parentSession.RootSessionId);
            var rootSession = await ReadSessionAsync(
                connection, transaction, rootIdentity, forUpdate: true, cancellationToken).ConfigureAwait(false);
            RequireWriter(rootSession, actor);
            RequireCurrentActiveSession(rootSession, run);
            await using var latestDecision = new NpgsqlCommand($"""
                SELECT state_version, execution_fence, decision::text
                FROM {_decisions}
                WHERE project_id = @project AND run_id = @run AND session_id = @root
                ORDER BY state_version DESC
                LIMIT 1
                """, connection, transaction);
            AddRunScope(latestDecision, parent.ProjectId, parent.RunId);
            latestDecision.Parameters.AddWithValue(
                "root", NpgsqlDbType.Varchar, parentSession.RootSessionId);
            await using var decisionReader = await latestDecision.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await decisionReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException(
                    "session_work_plan_item_stale", StatusCodes.Status409Conflict);
            var currentDecisionVersion = decisionReader.GetInt64(0);
            var currentDecisionFence = decisionReader.GetInt64(1);
            var currentDecisionJson = decisionReader.GetString(2);
            await decisionReader.DisposeAsync().ConfigureAwait(false);
            if (currentDecisionVersion != workPlanItemAssociation.DecisionStateVersion ||
                currentDecisionFence != run.Fence ||
                !RuntimeDecisionAllowsWorkPlanItem(
                    currentDecisionJson,
                    actor,
                    rootIdentity,
                    run.TenantId,
                    run.SelectionHash,
                    run.Fence,
                    workPlanItemId))
                throw new CoordinationException(
                    "session_work_plan_item_stale", StatusCodes.Status409Conflict);
        }
        else if (workPlanItemAssociation is not null)
        {
            throw new CoordinationException("session_spawn_invalid", StatusCodes.Status400BadRequest);
        }

        var commandHash = spawnRequest is null ? null : HashSpawnRequest(spawnRequest);
        if (spawnRequest is not null)
        {
            var replay = await ReadExistingSpawnAsync(
                connection,
                transaction,
                parent,
                actor,
                spawnRequest.IdempotencyKey,
                commandHash!,
                cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (persistSpawnedInTransaction is not null)
                    await persistSpawnedInTransaction(
                        connection, transaction, replay, cancellationToken).ConfigureAwait(false);
                if (revalidateCurrentAuthority is not null)
                    await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionRegistrationResult(
                    new RegisteredChild(
                        replay.Node.Identity,
                        parent.SessionId,
                        replay.PendingRequestId,
                        replay.Node.ExecutionFence),
                    replay);
            }
        }

        if (await IsForkTargetReservedAsync(
                connection,
                transaction,
                parent.ProjectId,
                parent.RunId,
                childSessionId,
                cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("child_session_conflict", StatusCodes.Status409Conflict);

        if (await IsAncestorSessionAsync(
                connection, transaction, parent, childSessionId, cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("session_relationship_cycle", StatusCodes.Status409Conflict);

        var existing = await FindSessionAsync(
            connection, transaction, parent.ProjectId, parent.RunId, childSessionId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.ParentSessionId != parent.SessionId ||
                existing.WriterIssuer != actor.Issuer || existing.WriterSubject != actor.Subject ||
                existing.LifecycleState != "active")
                throw new CoordinationException("child_session_conflict", StatusCodes.Status409Conflict);
            if (existing.WorkPlanItemId != workPlanItemAssociation?.WorkPlanItemId)
                throw new CoordinationException("child_session_conflict", StatusCodes.Status409Conflict);
            var requestId = await FindRequestIdAsync(
                connection, transaction, parent.ProjectId, parent.RunId,
                parent.SessionId, childSessionId, cancellationToken).ConfigureAwait(false);
            if (requestId is null)
                throw new InvalidOperationException("A registered child has no pending request.");
            if (revalidateCurrentAuthority is not null)
                await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken);
            var registered = new RegisteredChild(
                new SessionIdentity(parent.ProjectId, parent.RunId, childSessionId),
                parent.SessionId,
                requestId,
                existing.ExecutionFence);
            if (spawnRequest is not null)
                throw new CoordinationException("session_spawn_conflict", StatusCodes.Status409Conflict);
            return new SessionRegistrationResult(registered, null);
        }

        if (workPlanItemAssociation is { } association)
        {
            await using var existingWorkItem = new NpgsqlCommand($"""
                SELECT session_id
                FROM {_sessions}
                WHERE project_id = @project AND run_id = @run
                  AND parent_session_id = @parent AND work_plan_item_id = @item
                LIMIT 1
                FOR UPDATE
                """, connection, transaction);
            AddRunScope(existingWorkItem, parent.ProjectId, parent.RunId);
            existingWorkItem.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
            existingWorkItem.Parameters.AddWithValue(
                "item", NpgsqlDbType.Varchar, association.WorkPlanItemId);
            if (await existingWorkItem.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                throw new CoordinationException(
                    "session_work_plan_item_conflict", StatusCodes.Status409Conflict);
        }

        // The run lock serializes capacity checks, including forks reserved but not yet registered.
        await using (var countChildren = new NpgsqlCommand($"""
            SELECT count(*)::integer
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run
              AND parent_session_id IS NOT NULL
            """, connection, transaction))
        {
            AddRunScope(countChildren, parent.ProjectId, parent.RunId);
            var currentChildren = Convert.ToInt32(
                await countChildren.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            var pendingForks = await ReadPendingForkCountAsync(
                connection, transaction, parent.ProjectId, parent.RunId, cancellationToken).ConfigureAwait(false);
            if (currentChildren + pendingForks >= maxChildren)
                throw new CoordinationException(
                    "run_child_limit_exceeded", StatusCodes.Status409Conflict);
        }
        await using (var countActiveChildren = new NpgsqlCommand($"""
            SELECT count(*)::integer
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run
              AND parent_session_id IS NOT NULL AND lifecycle_state = 'active'
            """, connection, transaction))
        {
            AddRunScope(countActiveChildren, parent.ProjectId, parent.RunId);
            var activeChildren = Convert.ToInt32(
                await countActiveChildren.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            var pendingForks = await ReadPendingForkCountAsync(
                connection, transaction, parent.ProjectId, parent.RunId, cancellationToken).ConfigureAwait(false);
            if (activeChildren + pendingForks >= maxConcurrentChildren)
                throw new CoordinationException(
                    "run_concurrent_child_limit_exceeded", StatusCodes.Status409Conflict);
        }

        await using (var insertSession = new NpgsqlCommand($"""
            INSERT INTO {_sessions}
                (project_id, run_id, session_id, parent_session_id, root_session_id, node_kind,
                 work_plan_item_id, writer_issuer, writer_subject, execution_fence)
            VALUES (@project, @run, @session, @parent, @root, @kind, @workPlanItemId,
                @issuer, @subject, @fence)
            """, connection, transaction))
        {
            AddRunScope(insertSession, parent.ProjectId, parent.RunId);
            insertSession.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, childSessionId);
            insertSession.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
            insertSession.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, parentSession.RootSessionId);
            insertSession.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, ToDatabaseSessionKind(kind));
            insertSession.Parameters.AddWithValue(
                "workPlanItemId",
                NpgsqlDbType.Varchar,
                (object?)workPlanItemAssociation?.WorkPlanItemId ?? DBNull.Value);
            insertSession.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insertSession.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insertSession.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, run.Fence);
            await insertSession.ExecuteNonQueryAsync(cancellationToken);
        }

        var requestIdCreated = Guid.NewGuid().ToString("N");
        await using (var insertRequest = new NpgsqlCommand($"""
            INSERT INTO {_requests}
                (project_id, run_id, request_id, sender_session_id, recipient_session_id)
            VALUES (@project, @run, @request, @sender, @recipient)
            """, connection, transaction))
        {
            AddRunScope(insertRequest, parent.ProjectId, parent.RunId);
            insertRequest.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestIdCreated);
            insertRequest.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, parent.SessionId);
            insertRequest.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, childSessionId);
            await insertRequest.ExecuteNonQueryAsync(cancellationToken);
        }

        var registeredChild = new RegisteredChild(
            new SessionIdentity(parent.ProjectId, parent.RunId, childSessionId),
            parent.SessionId,
            requestIdCreated,
            run.Fence);
        SpawnedSession? spawned = null;
        if (spawnRequest is not null)
        {
            var childSession = await ReadSessionAsync(
                connection, transaction, registeredChild.Identity, forUpdate: false, cancellationToken)
                .ConfigureAwait(false);
            var node = ToSessionTreeNode(childSession, registeredChild.Identity);
            var commandId = Guid.NewGuid();
            spawned = new SpawnedSession(node, requestIdCreated, commandId, "requested");
            var resultJson = JsonSerializer.SerializeToElement(spawned, JsonOptions);
            await using (var insertCommand = new NpgsqlCommand($"""
                INSERT INTO {_schema}.coordination_tree_commands
                    (command_id, project_id, run_id, source_session_id, target_session_id,
                     actor_issuer, actor_subject, execution_fence, command_kind,
                     idempotency_key, command_hash, result)
                VALUES (@command, @project, @run, @source, @target,
                    @issuer, @subject, @fence, 'spawn', @key, @hash, @result)
                """, connection, transaction))
            {
                AddRunScope(insertCommand, parent.ProjectId, parent.RunId);
                insertCommand.Parameters.AddWithValue("command", NpgsqlDbType.Uuid, commandId);
                insertCommand.Parameters.AddWithValue("source", NpgsqlDbType.Varchar, parent.SessionId);
                insertCommand.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, childSessionId);
                insertCommand.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
                insertCommand.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
                insertCommand.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, run.Fence);
                insertCommand.Parameters.AddWithValue(
                    "key", NpgsqlDbType.Varchar, spawnRequest.IdempotencyKey);
                insertCommand.Parameters.AddWithValue("hash", NpgsqlDbType.Char, commandHash!);
                insertCommand.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, resultJson.GetRawText());
                await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var outboxPayload = JsonSerializer.SerializeToElement(
                new CoordinationSpawnCommand(commandId, parent, spawnRequest, requestIdCreated, run.Fence),
                JsonOptions);
            await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
                commandId,
                $"coordination/{parent.ProjectId}/{parent.RunId}/{parent.SessionId}",
                $"{parent.SessionId}:{actor.Issuer}:{actor.Subject}:{spawnRequest.IdempotencyKey}",
                "orchestrator.session.spawn_requested",
                EventVersion,
                outboxPayload,
                _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }

        if (spawned is not null && persistSpawnedInTransaction is not null)
            await persistSpawnedInTransaction(
                connection, transaction, spawned, cancellationToken).ConfigureAwait(false);
        if (revalidateCurrentAuthority is not null)
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return new SessionRegistrationResult(registeredChild, spawned);
    }

    /// <summary>
    /// Reads a prior spawn result by the caller's idempotency key and request hash.
    /// </summary>
    private async Task<SpawnedSession?> ReadExistingSpawnAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity parent,
        CoordinationActor actor,
        string idempotencyKey,
        string commandHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT command_kind, command_hash, result::text
            FROM {_schema}.coordination_tree_commands
            WHERE project_id = @project AND run_id = @run AND source_session_id = @source
                AND actor_issuer = @issuer AND actor_subject = @subject AND idempotency_key = @key
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, parent.ProjectId, parent.RunId);
        command.Parameters.AddWithValue("source", NpgsqlDbType.Varchar, parent.SessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.GetString(0) != "spawn" ||
            !string.Equals(reader.GetString(1).TrimEnd(), commandHash, StringComparison.Ordinal))
            throw new CoordinationException("idempotency_conflict", StatusCodes.Status409Conflict);
        try
        {
            return JsonSerializer.Deserialize<SpawnedSession>(reader.GetString(2), JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "session_spawn_result_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    /// <summary>
    /// Checks whether a session is already an ancestor of another session in the same run.
    /// </summary>
    private async Task<bool> IsAncestorSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity parent,
        string candidateSessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            WITH RECURSIVE ancestors(session_id, parent_session_id, path) AS (
                SELECT session_id, parent_session_id, ARRAY[session_id]::varchar(256)[]
                FROM {_sessions}
                WHERE project_id = @project AND run_id = @run AND session_id = @parent
                UNION ALL
                SELECT ancestor.session_id, ancestor.parent_session_id,
                    (ancestors.path || ancestor.session_id)::varchar(256)[]
                FROM ancestors
                JOIN {_sessions} AS ancestor
                  ON ancestor.project_id = @project
                 AND ancestor.run_id = @run
                 AND ancestor.session_id = ancestors.parent_session_id
                WHERE ancestors.parent_session_id IS NOT NULL
                  AND NOT ancestor.session_id = ANY(ancestors.path)
            )
            SELECT EXISTS (SELECT 1 FROM ancestors WHERE session_id = @candidate)
            """, connection, transaction);
        AddRunScope(command, parent.ProjectId, parent.RunId);
        command.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
        command.Parameters.AddWithValue("candidate", NpgsqlDbType.Varchar, candidateSessionId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    /// <summary>
    /// Validates a spawn request's identifiers, idempotency key, and bounded kickoff content.
    /// </summary>
    internal static void ValidateSpawnRequest(SpawnSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CoordinationIdentity.ValidateIdentity(request.SessionId, nameof(request.SessionId));
        CoordinationIdentity.ValidateIdentity(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (request.IdempotencyKey.Length > 128 ||
            !Enum.IsDefined(request.Kind) ||
            request.Kind == CoordinationSessionKind.Coordinator ||
            (request.WorkPlanItemId is not null && request.Kind != CoordinationSessionKind.ChildWork) ||
            string.IsNullOrWhiteSpace(request.Kickoff) ||
            request.Kickoff.Length > 20_000 ||
            request.Kickoff.Contains('\0') ||
            request.UserQuote?.Length > 4_000 ||
            request.CoordinatorInstructions?.Length > 4_000)
            throw new CoordinationException("session_spawn_invalid", StatusCodes.Status400BadRequest);
        if (request.WorkPlanItemId is { } workPlanItemId)
            CoordinationIdentity.ValidateIdentity(workPlanItemId, nameof(request.WorkPlanItemId));
    }

    /// <summary>
    /// Validates a fork request's source, target, event, cursor, and idempotency fields.
    /// </summary>
    internal static void ValidateForkRequest(CoordinationSessionForkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CoordinationIdentity.ValidateIdentity(request.TargetSessionId, nameof(request.TargetSessionId));
        CoordinationIdentity.ValidateIdentity(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (request.ExecutionFence < 1 ||
            request.IdempotencyKey.Length > 128 ||
            request.IdempotencyKey.Any(char.IsControl) ||
            request.SourceEventId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.SourceCursor) ||
            request.SourceCursor.Length > 2048 ||
            request.SourceCursor.Contains('\0') ||
            !Enum.IsDefined(request.Kind) ||
            request.Kind == CoordinationSessionKind.Coordinator)
            throw new CoordinationException("session_fork_invalid", StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Hashes the accepted selection snapshot for immutable owner bindings.
    /// </summary>
    internal static string HashSelection(EffectiveRunSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ValidateSelection(selection);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selection.Snapshot.GetRawText())));
    }

    /// <summary>
    /// Hashes every fork-command input that must remain fixed for idempotent retries.
    /// </summary>
    private static string HashForkCommand(
        SessionIdentity source,
        CoordinationSessionForkRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                command = "fork",
                source.ProjectId,
                source.RunId,
                source.SessionId,
                request.ExecutionFence,
                request.IdempotencyKey,
                request.TargetSessionId,
                request.SourceEventId,
                request.SourceCursor,
                request.Kind
            }, JsonOptions))));

    /// <summary>
    /// Requires an accepted selection hash to be a 64-character hexadecimal digest.
    /// </summary>
    private static void ValidateAcceptedSelectionHash(string acceptedSelectionHash)
    {
        if (string.IsNullOrWhiteSpace(acceptedSelectionHash) ||
            acceptedSelectionHash.Length != 64 ||
            !acceptedSelectionHash.All(Uri.IsHexDigit))
            throw new CoordinationException(
                "accepted_run_selection_invalid", StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Checks that child-count limits are within the supported ranges.
    /// </summary>
    private static void ValidateChildLimits(int maxChildren, int maxConcurrentChildren)
    {
        if (maxChildren is < 0 or > 100 || maxConcurrentChildren is < 1 or > 32)
            throw new CoordinationException("run_child_limit_invalid", StatusCodes.Status502BadGateway);
    }

    /// <summary>
    /// Returns the conflict code that prevents a fork from registering against current run or session state.
    /// </summary>
    private static string? GetForkUnavailableCode(
        AcceptedRunRow run,
        SessionRow sourceSession,
        CoordinationSessionForkRequest request,
        string acceptedSelectionHash)
    {
        if (!string.Equals(run.SelectionHash, acceptedSelectionHash, StringComparison.Ordinal))
            return "accepted_selection_stale";
        if (request.ExecutionFence != run.Fence || sourceSession.ExecutionFence != run.Fence)
            return "execution_fence_stale";
        if (sourceSession.LifecycleState != "active")
            return "session_unavailable";
        if (request.Kind == CoordinationSessionKind.ChildRun &&
            sourceSession.NodeKind != "operator_chat")
            return "child_run_spawn_requires_operator_chat";
        return null;
    }

    /// <summary>
    /// Hashes a spawn request so the same idempotency key cannot be reused for different content.
    /// </summary>
    private static string HashSpawnRequest(SpawnSessionRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(request, JsonOptions))));

    /// <summary>
    /// Reads the accepted root and all registered session nodes in a stable tree order.
    /// </summary>
    public async Task<SessionTreeSnapshot> ReadSessionTreeAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        var nodes = await ReadTreeNodesAsync(
            connection, transaction, identity.ProjectId, identity.RunId, session.RootSessionId, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionTreeSnapshot(session.RootSessionId, nodes);
    }

    /// <summary>Reads a run's session tree after the endpoint validates current project read authority.</summary>
    internal async Task<SessionTreeSnapshot> ReadProjectSessionTreeAsync(
        string tenantId,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        CoordinationIdentity.ValidateIdentity(tenantId, nameof(tenantId));
        CoordinationIdentity.ValidateIdentity(projectId, nameof(projectId));
        CoordinationIdentity.ValidateIdentity(runId, nameof(runId));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, projectId, runId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        // Projects supplies the tenant scope; never return a matching project/run row from another tenant.
        if (run.TenantId != tenantId)
            throw new CoordinationException("accepted_run_not_found", StatusCodes.Status404NotFound);
        var root = await ReadRootSessionAsync(
            connection, transaction, projectId, runId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        if (root is null)
            throw new CoordinationException("session_tree_unavailable", StatusCodes.Status503ServiceUnavailable);
        var nodes = await ReadTreeNodesAsync(
            connection, transaction, projectId, runId, root.SessionId, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionTreeSnapshot(root.SessionId, nodes);
    }

    /// <summary>
    /// Reads current work-plan children for MAF dispatch, including explicitly associated fixed-work children.
    /// </summary>
    /// <exception cref="CoordinationException">The caller is not the active root owner or child associations are ambiguous.</exception>
    internal async Task<ImmutableArray<MafExecutionChildSnapshot>> ReadMafExecutionChildrenAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        CancellationToken cancellationToken,
        ImmutableDictionary<string, MafExecutionFixedWorkAssociation>? fixedWorkAssociations = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        fixedWorkAssociations ??= ImmutableDictionary<string, MafExecutionFixedWorkAssociation>.Empty
            .WithComparers(StringComparer.Ordinal);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, parent.ProjectId, parent.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, parent, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        if (session.NodeKind != "coordinator" || session.RootSessionId != session.SessionId)
            throw new CoordinationException(
                "maf_execution_coordinator_unavailable", StatusCodes.Status409Conflict);

        await using var command = new NpgsqlCommand($"""
            SELECT session_id, work_plan_item_id, lifecycle_state, execution_fence, detached
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run
              AND parent_session_id = @parent AND root_session_id = @root
              AND node_kind = 'child_work'
              AND (work_plan_item_id IS NOT NULL OR session_id = ANY(@fixedChildren))
            ORDER BY COALESCE(work_plan_item_id, session_id), created_at, session_id
            """, connection, transaction);
        AddRunScope(command, parent.ProjectId, parent.RunId);
        command.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, session.RootSessionId);
        command.Parameters.AddWithValue(
            "fixedChildren",
            NpgsqlDbType.Array | NpgsqlDbType.Varchar,
            fixedWorkAssociations.Values.Select(association => association.ChildSessionId).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var children = ImmutableArray.CreateBuilder<MafExecutionChildSnapshot>();
        var associationIds = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var childSessionId = reader.GetString(0);
            var workPlanItemId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var associationId = workPlanItemId ?? fixedWorkAssociations.Values
                .SingleOrDefault(association =>
                    string.Equals(association.ChildSessionId, childSessionId, StringComparison.Ordinal))
                ?.AssociationId;
            if (associationId is null)
                throw new CoordinationException(
                    "maf_execution_child_identity_conflict", StatusCodes.Status409Conflict);
            if (!associationIds.Add(associationId))
                throw new CoordinationException(
                    "maf_execution_child_duplicate", StatusCodes.Status409Conflict);
            children.Add(new MafExecutionChildSnapshot(
                associationId,
                new SessionIdentity(parent.ProjectId, parent.RunId, childSessionId),
                ParseLifecycle(reader.GetString(2)),
                reader.GetInt64(3))
            {
                Detached = reader.GetBoolean(4)
            });
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return children.ToImmutable();
    }

    /// <summary>
    /// Counts registered children and pending forks while confirming the active coordinator owns the run.
    /// </summary>
    internal async Task<(int RegisteredChildren, int ActiveChildren)> ReadMafExecutionCapacityAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, parent.ProjectId, parent.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, parent, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        if (session.NodeKind != "coordinator" || session.RootSessionId != session.SessionId)
            throw new CoordinationException(
                "maf_execution_coordinator_unavailable", StatusCodes.Status409Conflict);

        var pendingForks = await ReadPendingForkCountAsync(
            connection, transaction, parent.ProjectId, parent.RunId, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FILTER (WHERE parent_session_id IS NOT NULL)::integer,
                   count(*) FILTER (
                       WHERE parent_session_id IS NOT NULL AND lifecycle_state = 'active')::integer
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run
            """, connection, transaction);
        AddRunScope(command, parent.ProjectId, parent.RunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Run child capacity could not be read.");
        var registeredChildren = checked(reader.GetInt32(0) + pendingForks);
        var activeChildren = checked(reader.GetInt32(1) + pendingForks);
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (registeredChildren, activeChildren);
    }

    /// <summary>
    /// Reads the current session and run state, including turn activity and interruption intent.
    /// </summary>
    /// <remarks>Runtime effects are reported as unavailable here; this owner store does not infer them from session rows.</remarks>
    public async Task<SessionStatusSnapshot> ReadSessionStatusAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        CancellationToken cancellationToken,
        AuthorizedRunSelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        var kind = ParseSessionKind(session.NodeKind);
        var lifecycle = ParseLifecycle(session.LifecycleState);
        if (selection is not null &&
            kind == CoordinationSessionKind.Coordinator &&
            lifecycle != CoordinationLifecycleState.Active &&
            (selection.Selection.ProjectId != identity.ProjectId ||
             selection.Selection.RunId != identity.RunId ||
             run.TenantId != selection.Authorization.TenantId ||
             !string.Equals(
                 run.SelectionHash,
                 HashSelection(selection.Selection),
                 StringComparison.Ordinal) ||
             session.ParentSessionId is not null ||
             session.RootSessionId != session.SessionId))
            throw new CoordinationException("coordinator_session_stale", StatusCodes.Status409Conflict);
        var (activity, activityUnavailableCode) = ToActivity(session.TurnState);
        var interruptionIntent = await ReadInterruptionIntentAsync(
            connection, transaction, identity, session.ExecutionFence, cancellationToken).ConfigureAwait(false);
        var checkpointRecovery = identity.SessionId == session.RootSessionId
            ? await ReadLatestCheckpointRecoveryStatusAsync(
                connection, transaction, identity.ProjectId, identity.RunId, cancellationToken)
                .ConfigureAwait(false)
            : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SessionStatusSnapshot(
            identity,
            session.ParentSessionId,
            session.RootSessionId,
            kind,
            session.Detached,
            activity,
            activityUnavailableCode,
            lifecycle,
            run.Fence,
            session.StateVersion,
            [],
            "unavailable",
            "runtime_effect_owner_unavailable",
            interruptionIntent,
            new OwnerRunExecutionSnapshot(
                run.ExecutionState,
                run.StateVersion,
                run.ExecutionCauseCode,
                run.ExecutionReference))
        {
            CheckpointRecovery = checkpointRecovery
        };
    }

    /// <summary>
    /// Reads the current run state for its accepted owner and requires the root to match the current execution fence.
    /// </summary>
    public async Task<OwnerRunStatus> ReadOwnerRunStatusAsync(
        CoordinationActor actor,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, projectId, runId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        RequireRunOwner(run, actor);
        var root = await ReadRootSessionAsync(
            connection, transaction, projectId, runId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        if (root is null || root.ExecutionFence != run.Fence)
            throw new CoordinationException("run_status_unavailable", StatusCodes.Status503ServiceUnavailable);
        var checkpointRecovery = await ReadLatestCheckpointRecoveryStatusAsync(
            connection, transaction, projectId, runId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OwnerRunStatus(
            projectId,
            runId,
            root.SessionId,
            run.Fence,
            run.LogicalTurnOrdinal,
            run.ExecutionState,
            run.StateVersion,
            run.ExecutionCauseCode,
            run.ExecutionReference)
        {
            CheckpointRecovery = checkpointRecovery
        };
    }

    private async Task<CheckpointRecoveryStatus?> ReadLatestCheckpointRecoveryStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT result -> 'checkpointRecovery'
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run
                AND operation_kind = 'recovery'
                AND result ? 'checkpointRecovery'
            ORDER BY created_at DESC
            LIMIT 1
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return json is null or DBNull
            ? null
            : JsonSerializer.Deserialize<CheckpointRecoveryStatus>(
                json is string value ? value : json.ToString()!, JsonOptions);
    }

    /// <summary>
    /// Reads a run's execution state and rejects missing or unsupported persisted state values.
    /// </summary>
    internal async Task<string> ReadCurrentExecutionStateAsync(
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        CoordinationIdentity.ValidateIdentity(projectId, nameof(projectId));
        CoordinationIdentity.ValidateIdentity(runId, nameof(runId));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT execution_state
            FROM {_runs}
            WHERE project_id = @project AND run_id = @run
            """, connection);
        AddRunScope(command, projectId, runId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string state)
            throw new CoordinationException(
                "run_status_unavailable", StatusCodes.Status404NotFound);
        if (state is not ("idle" or "active" or "blocked" or "completed" or "failed" or "indeterminate" or "cancelled"))
            throw new CoordinationException(
                "run_status_unavailable", StatusCodes.Status503ServiceUnavailable);
        return state;
    }

    /// <summary>
    /// Reads the active child-work binding required for an authenticated runtime observation.
    /// </summary>
    public async Task<SessionRuntimeOwnerState> ReadRuntimeOwnerStateAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        CancellationToken cancellationToken)
        => await ReadRuntimeOwnerStateAsync(actor, identity, allowTerminalChild: false, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Reads a current child-work binding while allowing a terminal turn for final runtime observations.
    /// </summary>
    public async Task<SessionRuntimeOwnerState> ReadRuntimeOwnerStateForObservationAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        CancellationToken cancellationToken)
        => await ReadRuntimeOwnerStateAsync(actor, identity, allowTerminalChild: true, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Reads retained root or child ownership for an authorized v2 diagnostic read, including terminal and older fences.
    /// </summary>
    internal async Task<SessionRuntimeOwnerState> ReadRuntimeOwnerStateForV2ObservationAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        string acceptedSelectionHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: false, cancellationToken,
            forShare: true).ConfigureAwait(false);
        if (run.TenantId != tenantId || run.SelectionHash != acceptedSelectionHash)
            throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);

        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        var root = await ReadRootSessionAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var isCoordinatorRoot = session.NodeKind == "coordinator" &&
            session.ParentSessionId is null && session.RootSessionId == identity.SessionId &&
            session.WorkPlanItemId is null;
        var isChild = session.NodeKind == "child_work" && session.ParentSessionId is not null &&
            root is not null && session.RootSessionId == root.SessionId;
        if (root is null || root.NodeKind != "coordinator" || root.SessionId != session.RootSessionId ||
            session.StateVersion < 1 || session.ExecutionFence < 1 ||
            (!isCoordinatorRoot && !isChild))
            throw new CoordinationException(
                "runtime_owner_context_unavailable", StatusCodes.Status409Conflict);

        var result = new SessionRuntimeOwnerState(
            session.RootSessionId, session.WorkPlanItemId, run.TenantId, run.SelectionHash, run.Fence,
            session.LogicalTurnOrdinal, session.StateVersion,
            isCoordinatorRoot
                ? CreateCoordinatorRootRuntimeTurnId(identity, run.Fence)
                : CreateRuntimeTurnId(identity, session.ExecutionFence, session.LogicalTurnOrdinal))
        {
            IsCoordinatorRoot = isCoordinatorRoot,
            SessionExecutionFence = session.ExecutionFence
        };
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Reads an accepted coordinator root or active child binding for runtime registration.
    /// </summary>
    internal Task<SessionRuntimeOwnerState> ReadRuntimeOwnerStateForRegistrationAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        string acceptedSelectionHash,
        CancellationToken cancellationToken,
        CheckpointRecoveryRegistrationAuthorization? checkpointRecoveryRegistration = null) =>
        ReadRuntimeOwnerStateAsync(
            actor, identity, allowTerminalChild: false, allowCoordinatorRoot: true,
            tenantId, acceptedSelectionHash, cancellationToken, checkpointRecoveryRegistration);

    /// <summary>
    /// Enforces actor, run-fence, child-work, turn, and version checks before returning runtime owner context.
    /// </summary>
    private async Task<SessionRuntimeOwnerState> ReadRuntimeOwnerStateAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        bool allowTerminalChild,
        CancellationToken cancellationToken) =>
        await ReadRuntimeOwnerStateAsync(
            actor, identity, allowTerminalChild, allowCoordinatorRoot: false,
            expectedTenantId: null, expectedSelectionHash: null, cancellationToken).ConfigureAwait(false);

    private async Task<SessionRuntimeOwnerState> ReadRuntimeOwnerStateAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        bool allowTerminalChild,
        bool allowCoordinatorRoot,
        string? expectedTenantId,
        string? expectedSelectionHash,
        CancellationToken cancellationToken,
        CheckpointRecoveryRegistrationAuthorization? checkpointRecoveryRegistration = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Native admission holds a shared run lock while it revalidates the owner.
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: false, cancellationToken,
            forShare: true)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        if (allowTerminalChild)
            RequireCurrentObservationSession(session, run);
        else
            RequireCurrentActiveSession(session, run);

        // Only the registration path may accept a coordinator root; other reads require child work.
        var isCoordinatorRoot = allowCoordinatorRoot &&
            session.NodeKind == "coordinator" &&
            session.ParentSessionId is null &&
            session.RootSessionId == identity.SessionId &&
            session.WorkPlanItemId is null &&
            run.ExecutionState is not ("completed" or "failed" or "indeterminate" or "cancelled") &&
            (expectedTenantId is null || run.TenantId == expectedTenantId) &&
            (expectedSelectionHash is null || run.SelectionHash == expectedSelectionHash);
        var isCurrentChildWork = session.NodeKind == "child_work" &&
            (session.TurnState == "active" ||
                allowTerminalChild && session.TurnState is "completed" or "failed" or "indeterminate") &&
            session.LogicalTurnOrdinal >= 1;
        if (isCoordinatorRoot || isCurrentChildWork && !allowTerminalChild)
        {
            if (checkpointRecoveryRegistration is { } recoveryRegistration)
                await RequireCheckpointRecoveryRegistrationAsync(
                    actor, identity, run.Fence, recoveryRegistration, cancellationToken).ConfigureAwait(false);
            else
                await RequireNoCheckpointRecoveryHoldAsync(
                    connection, transaction, identity.ProjectId, identity.RunId, cancellationToken)
                    .ConfigureAwait(false);
        }
        if ((!isCoordinatorRoot && !isCurrentChildWork) || session.StateVersion < 1)
            throw new CoordinationException(
                "runtime_owner_context_unavailable", StatusCodes.Status409Conflict);

        var result = new SessionRuntimeOwnerState(
            session.RootSessionId,
            session.WorkPlanItemId,
            run.TenantId,
            run.SelectionHash,
            run.Fence,
            session.LogicalTurnOrdinal,
            session.StateVersion,
            isCoordinatorRoot
                ? CreateCoordinatorRootRuntimeTurnId(identity, run.Fence)
                : CreateRuntimeTurnId(identity, run.Fence, session.LogicalTurnOrdinal))
        {
            IsCoordinatorRoot = isCoordinatorRoot
        };
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Allows an observation only for a current-fence session whose run has not been superseded.
    /// </summary>
    private static void RequireCurrentObservationSession(SessionRow session, AcceptedRunRow run)
    {
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        if (run.ExecutionState is "failed" or "indeterminate")
            throw new CoordinationException("execution_recovery_required", StatusCodes.Status409Conflict);
        if (session.LifecycleState is not ("active" or "completed" or "failed"))
            throw new CoordinationException("session_unavailable", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Checks that a persisted coordinator decision authorizes the requested work-plan item.
    /// </summary>
    private static bool RuntimeDecisionAllowsWorkPlanItem(
        string decisionJson,
        CoordinationActor actor,
        SessionIdentity root,
        string tenantId,
        string acceptedSelectionHash,
        long executionFence,
        string workPlanItemId)
    {
        try
        {
            using var document = JsonDocument.Parse(decisionJson);
            if (!document.RootElement.TryGetProperty("envelope", out var envelope) ||
                envelope.ValueKind != JsonValueKind.Object ||
                !HasNumber(envelope, "version", CoordinatorDecisionStateEnvelope.CurrentVersion) ||
                !HasString(envelope, "issuer", actor.Issuer) ||
                !HasString(envelope, "subject", actor.Subject) ||
                !HasString(envelope, "tenantId", tenantId) ||
                !HasString(envelope, "projectId", root.ProjectId) ||
                !HasString(envelope, "runId", root.RunId) ||
                !HasString(envelope, "rootSessionId", root.SessionId) ||
                !HasString(envelope, "acceptedSelectionHash", acceptedSelectionHash) ||
                !HasNumber(envelope, "fence", executionFence) ||
                !HasTrue(envelope, "outcomeConfirmed") ||
                !HasObject(envelope, "outcomeSpecification") ||
                !HasTrue(envelope, "workflowConfirmed") ||
                !HasObject(envelope, "selectedWorkflow") ||
                !HasNull(envelope, "pendingGate") ||
                !HasNull(envelope, "candidateWorkPlan") ||
                !envelope.TryGetProperty("confirmedWorkPlan", out var confirmedPlan) ||
                confirmedPlan.ValueKind != JsonValueKind.Object ||
                !confirmedPlan.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return false;

            return items.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), workPlanItemId, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }

        static bool HasString(JsonElement element, string propertyName, string expected) =>
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            string.Equals(property.GetString(), expected, StringComparison.Ordinal);

        static bool HasNumber(JsonElement element, string propertyName, long expected) =>
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out var value) &&
            value == expected;

        static bool HasTrue(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.True;

        static bool HasObject(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Object;

        static bool HasNull(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Null;
    }

    /// <summary>
    /// Reads the current interruption intent associated with the supplied session fence.
    /// </summary>
    private async Task<SessionInterruptionIntentSnapshot> ReadInterruptionIntentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        long executionFence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT message_id, status
            FROM {_messages}
            WHERE project_id = @project AND run_id = @run
                AND recipient_session_id = @session AND recipient_fence = @fence
                AND purpose = 'Steering' AND kind = 'Steering'
                AND payload ->> 'action' = 'stop' AND status <> 'cancelled'
            ORDER BY created_at DESC, message_id DESC
            LIMIT 1
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new SessionInterruptionIntentSnapshot(
                CoordinationInterruptionIntentState.None, null, null);

        var messageId = reader.GetGuid(0);
        var state = reader.GetString(1) switch
        {
            "pending" or "admitted" or "presented" => CoordinationInterruptionIntentState.Requested,
            "acknowledged" => CoordinationInterruptionIntentState.Acknowledged,
            _ => throw new CoordinationException(
                "session_interruption_state_unavailable", StatusCodes.Status503ServiceUnavailable)
        };
        return new SessionInterruptionIntentSnapshot(
            state,
            messageId,
            state == CoordinationInterruptionIntentState.Requested
                ? "operator_stop_requested"
                : "operator_stop_message_acknowledged");
    }

    /// <summary>
    /// Maps a persisted turn state to public activity, preserving unknown states as unavailable.
    /// </summary>
    private static (CoordinationActivityState Activity, string? UnavailableCode) ToActivity(string turnState) =>
        turnState switch
        {
            "presenting" or "active" => (CoordinationActivityState.Busy, null),
            "idle" or "blocked" or "completed" => (CoordinationActivityState.Idle, null),
            "failed" => (CoordinationActivityState.Unknown, "logical_turn_failed"),
            "indeterminate" => (CoordinationActivityState.Unknown, "logical_turn_indeterminate"),
            _ => (CoordinationActivityState.Unknown, "coordination_turn_state_unavailable")
        };

    /// <summary>
    /// Detaches a child using an idempotent tree command guarded by the execution fence.
    /// </summary>
    public async Task<CoordinationTreeCommandResult> DetachSessionAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        SessionTreeCommandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateTreeCommandRequest(request);
        var commandHash = HashTreeCommand("detach", identity, identity.SessionId, request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        if (request.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        var replay = await ReadExistingTreeCommandAsync(
            connection, transaction, identity, actor, request.IdempotencyKey,
            "detach", commandHash, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }
        if (session.ParentSessionId is null || session.Detached)
            throw new CoordinationException("session_detach_conflict", StatusCodes.Status409Conflict);

        var commandId = Guid.NewGuid();
        var result = new CoordinationTreeCommandResult(
            commandId, "detach", identity.SessionId, identity.SessionId, run.Fence, "detached");
        await using (var update = new NpgsqlCommand($"""
            WITH RECURSIVE descendants(session_id) AS (
                SELECT session_id
                FROM {_sessions}
                WHERE project_id = @project AND run_id = @run AND session_id = @target
                UNION ALL
                SELECT child.session_id
                FROM descendants AS parent
                JOIN {_sessions} AS child
                  ON child.project_id = @project AND child.run_id = @run
                 AND child.parent_session_id = parent.session_id
                WHERE child.detached = false
            )
            UPDATE {_sessions} AS session
            SET root_session_id = @target,
                detached = CASE WHEN session.session_id = @target THEN true ELSE session.detached END,
                state_version = state_version + 1
            WHERE session.project_id = @project AND session.run_id = @run
              AND session.session_id IN (SELECT session_id FROM descendants)
            """, connection, transaction))
        {
            AddRunScope(update, identity.ProjectId, identity.RunId);
            update.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, identity.SessionId);
            var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed < 1)
                throw new CoordinationException("session_detach_conflict", StatusCodes.Status409Conflict);
        }
        await PersistTreeCommandAsync(
            connection, transaction, identity, identity.SessionId, identity.SessionId, actor,
            request, commandHash, result, "orchestrator.session.detached", cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Archives a child using an idempotent tree command guarded by the execution fence.
    /// </summary>
    public async Task<CoordinationTreeCommandResult> ArchiveChildAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        string childSessionId,
        SessionTreeCommandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        CoordinationIdentity.ValidateIdentity(childSessionId, nameof(childSessionId));
        ValidateTreeCommandRequest(request);
        var commandHash = HashTreeCommand("archive", parent, childSessionId, request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, parent.ProjectId, parent.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var parentSession = await ReadSessionAsync(
            connection, transaction, parent, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(parentSession, actor);
        RequireCurrentActiveSession(parentSession, run);
        if (request.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        var childIdentity = new SessionIdentity(parent.ProjectId, parent.RunId, childSessionId);
        var child = await ReadSessionAsync(
            connection, transaction, childIdentity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(child, actor);
        if (child.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        var replay = await ReadExistingTreeCommandAsync(
            connection, transaction, parent, actor, request.IdempotencyKey,
            "archive", commandHash, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }
        if (child.ParentSessionId != parent.SessionId || child.Detached ||
            child.LifecycleState != "completed" || child.TurnState != "completed")
            throw new CoordinationException("session_archive_conflict", StatusCodes.Status409Conflict);

        var commandId = Guid.NewGuid();
        var result = new CoordinationTreeCommandResult(
            commandId, "archive", parent.SessionId, childSessionId, run.Fence, "archived");
        await using (var update = new NpgsqlCommand($"""
            UPDATE {_sessions}
            SET lifecycle_state = 'archived', archived_at = @archived, state_version = state_version + 1
            WHERE project_id = @project AND run_id = @run AND session_id = @child
                AND parent_session_id = @parent AND lifecycle_state = 'completed'
                AND turn_state = 'completed' AND detached = false
            """, connection, transaction))
        {
            AddRunScope(update, parent.ProjectId, parent.RunId);
            update.Parameters.AddWithValue("child", NpgsqlDbType.Varchar, childSessionId);
            update.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
            update.Parameters.AddWithValue("archived", NpgsqlDbType.TimestampTz, _timeProvider.GetUtcNow());
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CoordinationException("session_archive_conflict", StatusCodes.Status409Conflict);
        }
        await PersistTreeCommandAsync(
            connection, transaction, parent, parent.SessionId, childSessionId, actor,
            request, commandHash, result, "orchestrator.session.archived", cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Creates or updates an idle-notification subscription after checking the subscriber and target session bindings.
    /// </summary>
    /// <remarks>The stored subscription is fenced and uses an idempotency key to make retries stable.</remarks>
    public async Task<IdleSubscriptionResult> SubscribeToIdleAsync(
        CoordinationActor actor,
        SessionIdentity target,
        SubscribeToIdleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        CoordinationIdentity.ValidateIdentity(request.SubscriberSessionId, nameof(request.SubscriberSessionId));
        CoordinationIdentity.ValidateIdentity(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (request.IdempotencyKey.Length > 128 ||
            request.ExecutionFence < 1 ||
            !Enum.IsDefined(request.Mode))
            throw new CoordinationException("idle_subscription_invalid", StatusCodes.Status400BadRequest);

        var subscriber = new SessionIdentity(target.ProjectId, target.RunId, request.SubscriberSessionId);
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                target,
                request.SubscriberSessionId,
                request.ExecutionFence,
                request.IdempotencyKey,
                request.Mode
            }, JsonOptions))));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, target.ProjectId, target.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var targetSession = await ReadSessionAsync(
            connection, transaction, target, forUpdate: true, cancellationToken).ConfigureAwait(false);
        var subscriberSession = await ReadSessionAsync(
            connection, transaction, subscriber, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(targetSession, actor);
        RequireWriter(subscriberSession, actor);
        RequireCurrentActiveSession(targetSession, run);
        RequireCurrentActiveSession(subscriberSession, run);
        if (request.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        await using (var existing = new NpgsqlCommand($"""
            SELECT subscription_id, mode, execution_fence, command_hash, active
            FROM {_schema}.coordination_idle_subscriptions
            WHERE project_id = @project AND run_id = @run AND target_session_id = @target
                AND subscriber_session_id = @subscriber
                AND writer_issuer = @issuer AND writer_subject = @subject
                AND idempotency_key = @key
            FOR UPDATE
            """, connection, transaction))
        {
            AddRunScope(existing, target.ProjectId, target.RunId);
            existing.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, target.SessionId);
            existing.Parameters.AddWithValue("subscriber", NpgsqlDbType.Varchar, subscriber.SessionId);
            existing.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            existing.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            existing.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, request.IdempotencyKey);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(3).TrimEnd(), commandHash, StringComparison.Ordinal))
                    throw new CoordinationException("idempotency_conflict", StatusCodes.Status409Conflict);
                var replay = new IdleSubscriptionResult(
                    reader.GetGuid(0),
                    target.SessionId,
                    subscriber.SessionId,
                    ParseIdleMode(reader.GetString(1)),
                    reader.GetInt64(2),
                    reader.GetBoolean(4),
                    IdleNotificationSourceState.Available,
                    null);
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }
        }

        var subscriptionId = Guid.NewGuid();
        var result = new IdleSubscriptionResult(
            subscriptionId,
            target.SessionId,
            subscriber.SessionId,
            request.Mode,
            run.Fence,
            Active: true,
            IdleNotificationSourceState.Available,
            null);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.coordination_idle_subscriptions
                (subscription_id, project_id, run_id, target_session_id, subscriber_session_id,
                 writer_issuer, writer_subject, execution_fence, mode, idempotency_key, command_hash)
            VALUES (@id, @project, @run, @target, @subscriber,
                @issuer, @subject, @fence, @mode, @key, @hash)
            """, connection, transaction))
        {
            AddRunScope(insert, target.ProjectId, target.RunId);
            insert.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, subscriptionId);
            insert.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, target.SessionId);
            insert.Parameters.AddWithValue("subscriber", NpgsqlDbType.Varchar, subscriber.SessionId);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, run.Fence);
            insert.Parameters.AddWithValue("mode", NpgsqlDbType.Varchar, ToDatabaseIdleMode(request.Mode));
            insert.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, request.IdempotencyKey);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, commandHash);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var payload = JsonSerializer.SerializeToElement(result, JsonOptions);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            subscriptionId,
            $"coordination/{target.ProjectId}/{target.RunId}/{target.SessionId}",
            $"{target.SessionId}:{actor.Issuer}:{actor.Subject}:idle-subscription:{request.IdempotencyKey}",
            "orchestrator.session.idle_subscribed",
            EventVersion,
            payload,
            _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Reads and validates an existing session-tree command for an idempotent retry.
    /// </summary>
    private async Task<CoordinationTreeCommandResult?> ReadExistingTreeCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity source,
        CoordinationActor actor,
        string idempotencyKey,
        string commandKind,
        string commandHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT command_kind, command_hash, result::text
            FROM {_schema}.coordination_tree_commands
            WHERE project_id = @project AND run_id = @run AND source_session_id = @source
                AND actor_issuer = @issuer AND actor_subject = @subject AND idempotency_key = @key
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, source.ProjectId, source.RunId);
        command.Parameters.AddWithValue("source", NpgsqlDbType.Varchar, source.SessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.GetString(0) != commandKind ||
            !string.Equals(reader.GetString(1).TrimEnd(), commandHash, StringComparison.Ordinal))
            throw new CoordinationException("idempotency_conflict", StatusCodes.Status409Conflict);
        try
        {
            return JsonSerializer.Deserialize<CoordinationTreeCommandResult>(reader.GetString(2), JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "session_tree_command_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    /// <summary>
    /// Reads a fork command only when the stored command hash matches the retried request.
    /// </summary>
    private async Task<CoordinationSessionForkResult?> ReadExistingForkCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity source,
        CoordinationActor actor,
        string idempotencyKey,
        string commandHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT command_kind, command_hash, command_id, requested_target_session_id, result::text
            FROM {_schema}.coordination_tree_commands
            WHERE project_id = @project AND run_id = @run AND source_session_id = @source
                AND actor_issuer = @issuer AND actor_subject = @subject AND idempotency_key = @key
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, source.ProjectId, source.RunId);
        command.Parameters.AddWithValue("source", NpgsqlDbType.Varchar, source.SessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.GetString(0) != "fork" ||
            !string.Equals(reader.GetString(1).TrimEnd(), commandHash, StringComparison.Ordinal))
            throw new CoordinationException("idempotency_conflict", StatusCodes.Status409Conflict);
        try
        {
            var result = JsonSerializer.Deserialize<CoordinationSessionForkResult>(
                reader.GetString(4), JsonOptions) ?? throw new JsonException();
            if (result.CommandId != reader.GetGuid(2) ||
                result.Source != source ||
                result.TargetSessionId != reader.GetString(3))
                throw new JsonException();
            return result;
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "session_fork_operation_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    /// <summary>
    /// Stores a terminal unregistered fork result and releases its target reservation.
    /// </summary>
    private async Task<CoordinationSessionForkResult> FinalizeUnregisteredForkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity source,
        CoordinationActor actor,
        CoordinationSessionForkRequest request,
        string acceptedSelectionHash,
        CoordinationSessionForkResult existing,
        SessionForkLineage? lineage,
        string unavailableCode,
        CancellationToken cancellationToken)
    {
        var result = existing with
        {
            RegistrationState = CoordinationForkRegistrationState.Unregistered,
            Node = null,
            PendingRequestId = null,
            Lineage = lineage,
            UnavailableCode = unavailableCode,
            IsDuplicate = false
        };
        await UpdateForkCommandAsync(
            connection,
            transaction,
            source,
            result,
            targetSessionId: null,
            cancellationToken).ConfigureAwait(false);
        await EnqueueForkOutboxAsync(
            connection,
            transaction,
            source,
            actor,
            request,
            acceptedSelectionHash,
            result,
            "orchestrator.session.fork_unregistered",
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Updates the persisted fork command result and registration state.
    /// </summary>
    private async Task UpdateForkCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity source,
        CoordinationSessionForkResult result,
        string? targetSessionId,
        CancellationToken cancellationToken)
    {
        var resultJson = JsonSerializer.SerializeToElement(result with { IsDuplicate = false }, JsonOptions);
        await using var update = new NpgsqlCommand($"""
            UPDATE {_schema}.coordination_tree_commands
            SET target_session_id = @target, result = @result
            WHERE project_id = @project AND run_id = @run AND command_id = @command
            """, connection, transaction);
        AddRunScope(update, source.ProjectId, source.RunId);
        update.Parameters.AddWithValue("command", NpgsqlDbType.Uuid, result.CommandId);
        update.Parameters.AddWithValue(
            "target", NpgsqlDbType.Varchar, (object?)targetSessionId ?? DBNull.Value);
        update.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, resultJson.GetRawText());
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException(
                "session_fork_operation_unavailable", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Enqueues the owner event describing a completed fork registration.
    /// </summary>
    private async Task EnqueueForkOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity source,
        CoordinationActor actor,
        CoordinationSessionForkRequest request,
        string acceptedSelectionHash,
        CoordinationSessionForkResult result,
        string eventType,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            result = result with { IsDuplicate = false },
            actor = new { actor.Issuer, actor.Subject },
            request.ExecutionFence,
            request.IdempotencyKey,
            request.TargetSessionId,
            request.SourceEventId,
            request.SourceCursor,
            request.Kind,
            acceptedSelectionHash
        }, JsonOptions);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            result.CommandId,
            $"coordination/{source.ProjectId}/{source.RunId}/{source.SessionId}",
            $"{source.SessionId}:{actor.Issuer}:{actor.Subject}:fork:{request.IdempotencyKey}",
            eventType,
            EventVersion,
            payload,
            _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks whether a pending fork command already reserves the requested target session identifier.
    /// </summary>
    private async Task<bool> IsForkTargetReservedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        string targetSessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1
                FROM {_schema}.coordination_tree_commands
                WHERE project_id = @project AND run_id = @run
                  AND command_kind = 'fork' AND requested_target_session_id = @target
                  AND result ->> 'registrationState' IN ('registrationPending', 'unregistered')
            )
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, targetSessionId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    /// <summary>
    /// Counts fork commands that still reserve child capacity.
    /// </summary>
    private async Task<int> ReadPendingForkCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT count(*)::integer
            FROM {_schema}.coordination_tree_commands
            WHERE project_id = @project AND run_id = @run
              AND command_kind = 'fork'
              AND result ->> 'registrationState' = 'registrationPending'
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns a conflict code when registered children and pending forks exceed either run capacity limit.
    /// </summary>
    private async Task<string?> ReadForkCapacityErrorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        int maxChildren,
        int maxConcurrentChildren,
        int pendingForksToAdd,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*)::integer FROM {_sessions}
                 WHERE project_id = @project AND run_id = @run AND parent_session_id IS NOT NULL) +
                (SELECT count(*)::integer FROM {_schema}.coordination_tree_commands
                 WHERE project_id = @project AND run_id = @run AND command_kind = 'fork'
                   AND result ->> 'registrationState' = 'registrationPending') + @pending,
                (SELECT count(*)::integer FROM {_sessions}
                 WHERE project_id = @project AND run_id = @run
                   AND parent_session_id IS NOT NULL AND lifecycle_state = 'active') +
                (SELECT count(*)::integer FROM {_schema}.coordination_tree_commands
                 WHERE project_id = @project AND run_id = @run AND command_kind = 'fork'
                   AND result ->> 'registrationState' = 'registrationPending') + @pending
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("pending", NpgsqlDbType.Integer, pendingForksToAdd);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Fork capacity could not be read.");
        if (reader.GetInt32(0) > maxChildren)
            return "run_child_limit_exceeded";
        if (reader.GetInt32(1) > maxConcurrentChildren)
            return "run_concurrent_child_limit_exceeded";
        return null;
    }

    /// <summary>
    /// Ensures the Events fork result is bound to the expected source, target, event, cursor, and fork kind.
    /// </summary>
    private static void ValidateEventsForkResult(
        SessionIdentity source,
        CoordinationSessionForkRequest request,
        SessionForkResult eventsFork)
    {
        if (eventsFork?.Target is null || eventsFork.Lineage is null ||
            eventsFork.Target.Identity != new SessionIdentity(
                source.ProjectId, source.RunId, request.TargetSessionId) ||
            eventsFork.Lineage.Source != source ||
            eventsFork.Lineage.SourceEventId != request.SourceEventId ||
            eventsFork.Lineage.SourceSequence < 1 ||
            eventsFork.Lineage.CursorVersion < 1 ||
            eventsFork.Lineage.SourceSchemaVersion < 1 ||
            eventsFork.Lineage.SourceEventVersion < 1 ||
            !string.Equals(eventsFork.Lineage.SourceCursor, request.SourceCursor, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(eventsFork.Lineage.ProviderBindingHash) ||
            eventsFork.Lineage.ProviderBindingHash.Length != 64 ||
            !eventsFork.Lineage.ProviderBindingHash.All(Uri.IsHexDigit))
            throw new CoordinationException(
                "session_fork_contract_invalid", StatusCodes.Status502BadGateway);
    }

    /// <summary>
    /// Persists a session-tree command and its deterministic hash for safe retries.
    /// </summary>
    private async Task PersistTreeCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity source,
        string sourceSessionId,
        string? targetSessionId,
        CoordinationActor actor,
        SessionTreeCommandRequest request,
        string commandHash,
        CoordinationTreeCommandResult result,
        string eventType,
        CancellationToken cancellationToken)
    {
        var resultJson = JsonSerializer.SerializeToElement(result, JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.coordination_tree_commands
                (command_id, project_id, run_id, source_session_id, target_session_id,
                 actor_issuer, actor_subject, execution_fence, command_kind,
                 idempotency_key, command_hash, result)
            VALUES (@command, @project, @run, @source, @target,
                @issuer, @subject, @fence, @kind, @key, @hash, @result)
            """, connection, transaction))
        {
            AddRunScope(insert, source.ProjectId, source.RunId);
            insert.Parameters.AddWithValue("command", NpgsqlDbType.Uuid, result.CommandId);
            insert.Parameters.AddWithValue("source", NpgsqlDbType.Varchar, sourceSessionId);
            insert.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, (object?)targetSessionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, request.ExecutionFence);
            insert.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, result.Command);
            insert.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, request.IdempotencyKey);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, commandHash);
            insert.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, resultJson.GetRawText());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            result,
            actor = new { actor.Issuer, actor.Subject },
            request.ExecutionFence,
            request.IdempotencyKey
        }, JsonOptions);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            result.CommandId,
            $"coordination/{source.ProjectId}/{source.RunId}/{sourceSessionId}",
            $"{sourceSessionId}:{actor.Issuer}:{actor.Subject}:{request.IdempotencyKey}",
            eventType,
            EventVersion,
            payload,
            _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hashes the tree-command kind, actor, source, target, fence, and idempotency key.
    /// </summary>
    private static string HashTreeCommand(
        string command,
        SessionIdentity source,
        string targetSessionId,
        SessionTreeCommandRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                command,
                source.ProjectId,
                source.RunId,
                source.SessionId,
                targetSessionId,
                request.ExecutionFence,
                request.IdempotencyKey
            }, JsonOptions))));

    /// <summary>
    /// Validates the positive execution fence and bounded idempotency key for a tree command.
    /// </summary>
    private static void ValidateTreeCommandRequest(SessionTreeCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CoordinationIdentity.ValidateIdentity(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (request.ExecutionFence < 1 || request.IdempotencyKey.Length > 128)
            throw new CoordinationException("session_tree_command_invalid", StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Reads and maps the nodes belonging to one root session.
    /// </summary>
    private async Task<ImmutableArray<SessionTreeNode>> ReadTreeNodesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        string rootSessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT session_id, parent_session_id, root_session_id, node_kind, detached,
                writer_issuer, writer_subject, execution_fence, lifecycle_state, logical_turn_ordinal,
                turn_state, state_version, pending_wake, turn_boundary_request_version,
                turn_boundary_result, created_at, archived_at, work_plan_item_id
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run AND root_session_id = @root
            ORDER BY created_at, session_id
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, rootSessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var nodes = ImmutableArray.CreateBuilder<SessionTreeNode>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var session = ReadSessionRow(reader);
            nodes.Add(ToSessionTreeNode(
                session,
                new SessionIdentity(projectId, runId, session.SessionId)));
        }
        if (nodes.Count == 0)
            throw new CoordinationException("session_tree_unavailable", StatusCodes.Status503ServiceUnavailable);
        return nodes.ToImmutable();
    }

    /// <summary>
    /// Reads work-plan item associations for children of a root in the caller's transaction.
    /// </summary>
    internal async Task<ImmutableArray<(string SessionId, string WorkPlanItemId)>>
        ReadBacklogWorkPlanChildBindingsInTransactionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string projectId,
            string runId,
            string rootSessionId,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        CoordinationIdentity.ValidateIdentity(projectId, nameof(projectId));
        CoordinationIdentity.ValidateIdentity(runId, nameof(runId));
        CoordinationIdentity.ValidateIdentity(rootSessionId, nameof(rootSessionId));

        await using var command = new NpgsqlCommand($"""
            SELECT session_id, work_plan_item_id
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run AND root_session_id = @root
              AND node_kind = 'child_work' AND work_plan_item_id IS NOT NULL
            ORDER BY work_plan_item_id, session_id
            FOR SHARE
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, rootSessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var bindings = ImmutableArray.CreateBuilder<(string SessionId, string WorkPlanItemId)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sessionId = reader.GetString(0);
            var workPlanItemId = reader.GetString(1);
            try
            {
                CoordinationIdentity.ValidateIdentity(sessionId, nameof(sessionId));
                CoordinationIdentity.ValidateIdentity(workPlanItemId, nameof(workPlanItemId));
            }
            catch (CoordinationException exception)
            {
                throw new CoordinationException(
                    "backlog_prerequisite_binding_corrupt",
                    StatusCodes.Status503ServiceUnavailable,
                    exception);
            }
            bindings.Add((sessionId, workPlanItemId));
        }
        return bindings.ToImmutable();
    }

    /// <summary>
    /// Validates a message route against current owner membership and returns the pinned delivery binding.
    /// </summary>
    /// <exception cref="CoordinationException">The sender, recipient, run fence, or message purpose is not permitted.</exception>
    public async Task<MessageRouteBinding> ValidateMessageRouteAsync(
        CoordinationActor actor,
        SessionIdentity sender,
        MessageRouteValidationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Message);
        var outbound = request.Message;
        if (outbound.OwnerMessageId == Guid.Empty || outbound.Sender != sender ||
            outbound.Recipient.ProjectId != sender.ProjectId ||
            outbound.Recipient.RunId != sender.RunId)
            throw new CoordinationException("owner_message_invalid", StatusCodes.Status400BadRequest);

        var isRunUserSelfDelivery = outbound.Sender == outbound.Recipient &&
            AddressedMessageValidation.IsRunUserMessage(ToAddressedMessageDraft(outbound));
        if (outbound.Sender == outbound.Recipient &&
            outbound.IdempotencyKey != $"{sender.RunId}:first" &&
            !isRunUserSelfDelivery)
            throw new CoordinationException("first_message_invalid", StatusCodes.Status409Conflict);

        var persistedOutbound = await ReadOutboundMessageAsync(
            outbound.OwnerMessageId, cancellationToken).ConfigureAwait(false);
        if (!SameOutboundMessage(persistedOutbound, outbound))
            throw new CoordinationException("owner_message_mismatch", StatusCodes.Status409Conflict);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, sender.ProjectId, sender.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        if (isRunUserSelfDelivery)
        {
            var root = await ReadRootSessionAsync(
                connection, transaction, sender.ProjectId, sender.RunId, forUpdate: false, cancellationToken)
                .ConfigureAwait(false);
            if (root is null || root.SessionId != sender.SessionId || root.LifecycleState != "active")
                throw new CoordinationException("first_message_invalid", StatusCodes.Status409Conflict);
        }
        var stored = await ReadMessageAsync(
            connection, transaction, outbound.OwnerMessageId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        // Delivery progress must not prevent Events from returning the message's original idempotent receipt.
        var isPresentedReplay = stored?.Status is "presented" or "acknowledged";
        if (stored is null || stored.ProjectId != sender.ProjectId || stored.RunId != sender.RunId ||
            stored.SenderSessionId != sender.SessionId ||
            stored.RecipientSessionId != outbound.Recipient.SessionId ||
            stored.ThreadId != outbound.ThreadId ||
            stored.WriterIssuer != actor.Issuer || stored.WriterSubject != actor.Subject ||
            (stored.Status is not ("pending" or "admitted") && !isPresentedReplay))
            throw new CoordinationException("owner_message_unavailable", StatusCodes.Status409Conflict);

        var source = await ReadSessionAsync(connection, transaction, sender, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        RequireWriter(source, actor);
        RequireCurrentActiveSession(source, run);
        if (stored.SenderFence != source.ExecutionFence || stored.SenderFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        var recipientIdentity = outbound.Recipient;
        var recipient = await ReadSessionAsync(
            connection, transaction, recipientIdentity, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireCurrentActiveSession(recipient, run);
        if (stored.RecipientFence != recipient.ExecutionFence ||
            stored.RecipientFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        Guid? replyToMessageId = null;
        if (outbound.ReplyToId is { } replyTo)
        {
            var correlated = await HasReverseMessageAsync(
                connection, transaction, sender, recipientIdentity, replyTo,
                outbound.RequestId, outbound.ReplyCorrelationId, cancellationToken).ConfigureAwait(false);
            if (!correlated)
                throw new CoordinationException("reply_correlation_invalid", StatusCodes.Status409Conflict);
            replyToMessageId = await ReadAcknowledgedReplyMessageIdAsync(
                connection,
                transaction,
                sender,
                recipientIdentity,
                replyTo,
                outbound.RequestId,
                outbound.ReplyCorrelationId,
                cancellationToken).ConfigureAwait(false);
            if (replyToMessageId is null)
                throw new CoordinationException("reply_correlation_invalid", StatusCodes.Status409Conflict);
        }

        if (outbound.RequestId is { } requestId)
        {
            if (!await HasActiveRequestAsync(
                connection, transaction, sender.ProjectId, sender.RunId,
                sender.SessionId, recipientIdentity.SessionId, requestId, cancellationToken).ConfigureAwait(false) &&
                !await HasActiveRequestAsync(
                    connection, transaction, sender.ProjectId, sender.RunId,
                    recipientIdentity.SessionId, sender.SessionId, requestId, cancellationToken).ConfigureAwait(false))
                throw new CoordinationException("request_correlation_invalid", StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return new MessageRouteBinding(
            sender,
            recipientIdentity,
            source.ExecutionFence,
            recipient.ExecutionFence,
            CoordinationIdentity.ClaimOwner(actor),
            replyToMessageId);
    }

    /// <summary>
    /// Returns the owner-verified parent, root, lifecycle, and execution-fence binding for one session.
    /// </summary>
    public async Task<CoordinationSessionBinding> GetSessionBindingAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        await transaction.CommitAsync(cancellationToken);
        return new CoordinationSessionBinding(
            identity,
            session.ExecutionFence,
            session.TurnState,
            session.StateVersion,
            session.PendingWake,
            CoordinationIdentity.ClaimOwner(actor));
    }

    /// <summary>
    /// Claims the next outbound coordination event for delivery.
    /// </summary>
    public async Task<OutboxDelivery?> ClaimOutboundAsync(
        CoordinationActor actor,
        Guid ownerMessageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT writer_issuer, writer_subject
            FROM {_messages}
            WHERE message_id = @message
            """, connection);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, ownerMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new CoordinationException("message_not_found", StatusCodes.Status404NotFound);
        if (reader.GetString(0) != actor.Issuer || reader.GetString(1) != actor.Subject)
            throw new CoordinationException("message_writer_mismatch", StatusCodes.Status403Forbidden);
        await reader.DisposeAsync();
        return await _outbox.ClaimOneAsync(
            CoordinationIdentity.ClaimOwner(actor),
            ownerMessageId,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the owner message referenced by an outbox delivery.
    /// </summary>
    public async Task<OwnerOutboundMessage> ReadOutboundMessageAsync(
        Guid ownerMessageId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text
            FROM {_schema}.outbox_events
            WHERE id = @message AND event_type = 'orchestrator.addressed_message'
            """, connection);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, ownerMessageId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (json is null)
            throw new CoordinationException("message_outbox_not_found", StatusCodes.Status404NotFound);
        try
        {
            return JsonSerializer.Deserialize<OwnerOutboundMessage>(json, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new CoordinationException("message_outbox_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
    }

    /// <summary>
    /// Acknowledges a claimed outbound event using the outbox lease fence.
    /// </summary>
    public Task<bool> AcknowledgeOutboundAsync(
        OutboxDelivery delivery,
        CancellationToken cancellationToken) =>
        _outbox.AcknowledgeAsync(
            delivery.Event.Message.Id, delivery.LeaseToken, cancellationToken);

    /// <summary>Reads the accepted run's registered coordinator session.</summary>
    public async Task<string> ReadProjectRunRootSessionIdAsync(
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        CoordinationIdentity.ValidateIdentity(projectId, nameof(projectId));
        CoordinationIdentity.ValidateIdentity(runId, nameof(runId));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        _ = await ReadAcceptedRunAsync(
            connection, transaction, projectId, runId, forUpdate: false, cancellationToken).ConfigureAwait(false);
        var root = await ReadRootSessionAsync(
            connection, transaction, projectId, runId, forUpdate: false, cancellationToken).ConfigureAwait(false);
        if (root is null || root.SessionId != root.RootSessionId || root.NodeKind != "coordinator")
            throw new CoordinationException("run_root_unavailable", StatusCodes.Status404NotFound);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return root.SessionId;
    }

    /// <summary>
    /// Creates an owner-addressed message and outbox event after validating the bound route and request.
    /// </summary>
    /// <remarks>Idempotent retries return the stored result; reusing a key for different message content is rejected.</remarks>
    public async Task<CoordinationMessageResult> SendMessageAsync(
        CoordinationActor actor,
        SessionIdentity sender,
        CoordinationMessageRequest request,
        CancellationToken cancellationToken,
        bool allowCoordinatorSelfDelivery = false)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        CoordinationIdentity.ValidateIdentity(request.RecipientSessionId, nameof(request.RecipientSessionId));
        var recipient = new SessionIdentity(sender.ProjectId, sender.RunId, request.RecipientSessionId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, sender.ProjectId, sender.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var source = await ReadSessionAsync(connection, transaction, sender, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        RequireWriter(source, actor);
        RequireCurrentActiveSession(source, run);
        await RequireNoCheckpointRecoveryHoldAsync(
            connection, transaction, sender.ProjectId, sender.RunId, cancellationToken).ConfigureAwait(false);
        var target = await ReadSessionAsync(connection, transaction, recipient, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        RequireCurrentActiveSession(target, run);
        if (sender == recipient && !allowCoordinatorSelfDelivery)
            await RequirePersistedFirstMessageAsync(
                connection, transaction, actor, run.TenantId, sender, request, cancellationToken)
                .ConfigureAwait(false);

        Guid? replyThreadId = null;
        if (request.ReplyToId is { } replyTo)
        {
            replyThreadId = await ReadReverseMessageThreadIdAsync(
                connection, transaction, sender, recipient, replyTo,
                request.RequestId, request.ReplyCorrelationId, cancellationToken).ConfigureAwait(false);
            if (replyThreadId is null ||
                (request.ThreadId is { } requestedThread && requestedThread != replyThreadId))
                throw new CoordinationException("reply_correlation_invalid", StatusCodes.Status409Conflict);
        }
        if (request.RequestId is { } correlatedRequestId &&
            !await HasActiveRequestAsync(
                connection, transaction, sender.ProjectId, sender.RunId,
                sender.SessionId, recipient.SessionId, correlatedRequestId, cancellationToken).ConfigureAwait(false) &&
            !await HasActiveRequestAsync(
                connection, transaction, sender.ProjectId, sender.RunId,
                recipient.SessionId, sender.SessionId, correlatedRequestId, cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("request_correlation_invalid", StatusCodes.Status409Conflict);

        var threadId = request.ThreadId ?? replyThreadId ?? Guid.NewGuid();
        AddressedMessageDraft draft;
        try
        {
            draft = AddressedMessageValidation.ValidateAndNormalize(new AddressedMessageDraft(
                sender,
                recipient,
                request.IdempotencyKey,
                request.DeliveryMode,
                request.Purpose,
                request.Kind,
                request.Payload,
                source.ExecutionFence,
                target.ExecutionFence,
                threadId,
                request.ReplyToId,
                request.RequestId,
                request.ReplyCorrelationId,
                request.UserQuote,
                request.CoordinatorInstructions));
        }
        catch (ArgumentException exception)
        {
            throw new CoordinationException("addressed_message_invalid", StatusCodes.Status400BadRequest, exception);
        }

        // Only the run-level user route may send plain text from the coordinator to itself.
        if (sender == recipient && allowCoordinatorSelfDelivery &&
            (sender.SessionId != source.RootSessionId ||
             !AddressedMessageValidation.IsRunUserMessage(draft)))
            await RequirePersistedFirstMessageAsync(
                connection, transaction, actor, run.TenantId, sender, request, cancellationToken)
                .ConfigureAwait(false);

        var canonicalRequest = JsonSerializer.SerializeToElement(
            draft with { SenderFence = 0, RecipientFence = 0, ThreadId = request.ThreadId }, JsonOptions);
        var idempotencyKey = draft.IdempotencyKey;
        var messageId = Guid.NewGuid();
        var outbound = new OwnerOutboundMessage(
            messageId,
            sender,
            recipient,
            idempotencyKey,
            draft.DeliveryMode,
            draft.Purpose,
            draft.Kind,
            draft.Payload,
            draft.SenderFence,
            draft.RecipientFence,
            threadId,
            draft.ReplyToId,
            draft.RequestId,
            draft.ReplyCorrelationId,
            draft.UserQuote,
            draft.CoordinatorInstructions);

        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_messages}
                (project_id, run_id, message_id, sender_session_id, recipient_session_id,
                 writer_issuer, writer_subject, thread_id, sender_fence, recipient_fence,
                 idempotency_key, purpose, kind, delivery_mode, request_id, reply_to_id,
                 reply_correlation_id, canonical_request, payload)
            VALUES (@project, @run, @id, @sender, @recipient, @issuer, @subject, @thread,
                @senderFence, @recipientFence, @key, @purpose, @kind, @mode, @request,
                @replyTo, @replyCorrelation, @canonical, @payload)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            AddRunScope(insert, sender.ProjectId, sender.RunId);
            insert.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, messageId);
            insert.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
            insert.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipient.SessionId);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("thread", NpgsqlDbType.Uuid, threadId);
            insert.Parameters.AddWithValue("senderFence", NpgsqlDbType.Bigint, draft.SenderFence);
            insert.Parameters.AddWithValue("recipientFence", NpgsqlDbType.Bigint, draft.RecipientFence);
            insert.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
            insert.Parameters.AddWithValue("purpose", NpgsqlDbType.Varchar, draft.Purpose.ToString());
            insert.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, draft.Kind.ToString());
            insert.Parameters.AddWithValue("mode", NpgsqlDbType.Varchar, draft.DeliveryMode.ToString());
            insert.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, (object?)draft.RequestId ?? DBNull.Value);
            insert.Parameters.AddWithValue("replyTo", NpgsqlDbType.Uuid, (object?)draft.ReplyToId ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "replyCorrelation", NpgsqlDbType.Varchar, (object?)draft.ReplyCorrelationId ?? DBNull.Value);
            insert.Parameters.AddWithValue("canonical", NpgsqlDbType.Jsonb, canonicalRequest.GetRawText());
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, draft.Payload.GetRawText());
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
            if (inserted == 0)
            {
                var existing = await ReadExistingMessageAsync(
                    connection, transaction, sender, actor, idempotencyKey, canonicalRequest, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null || !existing.CanonicalRequestMatches)
                    throw new CoordinationException("idempotency_conflict", StatusCodes.Status409Conflict);
                await transaction.CommitAsync(cancellationToken);
                return new CoordinationMessageResult(
                    existing.MessageId, existing.RecipientSessionId, existing.Status, existing.RequestId);
            }
        }

        var payload = JsonSerializer.SerializeToElement(outbound, JsonOptions);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            messageId,
            $"coordination/{sender.ProjectId}/{sender.RunId}/{sender.SessionId}",
            $"{sender.SessionId}:{actor.Issuer}:{actor.Subject}:{idempotencyKey}",
            "orchestrator.addressed_message",
            EventVersion,
            payload,
            _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);

        if (draft.RequestId is { } requestId && draft.ReplyToId is null)
        {
            await using var bindRequest = new NpgsqlCommand($"""
                UPDATE {_requests}
                SET source_message_id = @message
                WHERE project_id = @project AND run_id = @run AND request_id = @request
                    AND ((sender_session_id = @sender AND recipient_session_id = @recipient)
                      OR (sender_session_id = @recipient AND recipient_session_id = @sender))
                    AND source_message_id IS NULL AND gate_state = 'pending'
                """, connection, transaction);
            AddRunScope(bindRequest, sender.ProjectId, sender.RunId);
            bindRequest.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, messageId);
            bindRequest.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestId);
            bindRequest.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
            bindRequest.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipient.SessionId);
            var bound = await bindRequest.ExecuteNonQueryAsync(cancellationToken);
            if (bound == 0)
                throw new CoordinationException("request_correlation_invalid", StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CoordinationMessageResult(messageId, recipient.SessionId, "pending", draft.RequestId);
    }

    /// <summary>
    /// Records an Events admission receipt and updates the corresponding owner message delivery state.
    /// </summary>
    /// <remarks>The consumer receipt and local message update share a transaction so duplicate delivery is safe.</remarks>
    public async Task AdmitDeliveryAsync(
        CoordinationActor actor,
        DeliveryIngressRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        var receipt = request.Admission ?? throw new CoordinationException(
            "delivery_receipt_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stored = await ReadMessageAsync(
            connection, transaction, request.OwnerMessageId, forUpdate: false, cancellationToken).ConfigureAwait(false);
        if (stored is null || request.OwnerMessageId != receipt.OwnerMessageId ||
            stored.WriterIssuer != actor.Issuer || stored.WriterSubject != actor.Subject ||
            !ReceiptMatches(stored, receipt))
            throw new CoordinationException("delivery_receipt_mismatch", StatusCodes.Status409Conflict);

        await RequireNoCheckpointRecoveryHoldAsync(
            connection, transaction, stored.ProjectId, stored.RunId, cancellationToken).ConfigureAwait(false);
        stored = await ReadMessageAsync(
            connection, transaction, request.OwnerMessageId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.WriterIssuer != actor.Issuer || stored.WriterSubject != actor.Subject ||
            !ReceiptMatches(stored, receipt))
            throw new CoordinationException("delivery_receipt_mismatch", StatusCodes.Status409Conflict);

        var admission = await _outbox.AdmitAsync(
            connection, transaction, IngressConsumer, receipt.MessageId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        if (admission == InboxAdmission.Duplicate)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using (var updateMessage = new NpgsqlCommand($"""
            UPDATE {_messages}
            SET status = 'admitted', events_message_id = @eventsMessage
            WHERE project_id = @project AND message_id = @ownerMessage AND status = 'pending'
            """, connection, transaction))
        {
            updateMessage.Parameters.AddWithValue("eventsMessage", NpgsqlDbType.Uuid, receipt.MessageId);
            updateMessage.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, stored.ProjectId);
            updateMessage.Parameters.AddWithValue("ownerMessage", NpgsqlDbType.Uuid, request.OwnerMessageId);
            if (await updateMessage.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new CoordinationException("delivery_state_conflict", StatusCodes.Status409Conflict);
        }

        if (stored.RequestId is not null && stored.ReplyToId is not null)
        {
            await using var updateRequest = new NpgsqlCommand($"""
                UPDATE {_requests}
                SET gate_state = 'input_available', updated_at = clock_timestamp()
                WHERE project_id = @project AND run_id = @run AND request_id = @request
                    AND gate_state = 'pending'
                """, connection, transaction);
            AddRunScope(updateRequest, stored.ProjectId, stored.RunId);
            updateRequest.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, stored.RequestId);
            await updateRequest.ExecuteNonQueryAsync(cancellationToken);
        }

        var wakes = receipt.Purpose is AddressedMessagePurpose.NeedsInput or AddressedMessagePurpose.Error;
        if (wakes)
        {
            await using var wakeRecipient = new NpgsqlCommand($"""
                UPDATE {_sessions}
                SET pending_wake = true, state_version = state_version + 1
                WHERE project_id = @project AND run_id = @run AND session_id = @recipient
                    AND lifecycle_state = 'active'
                """, connection, transaction);
            AddRunScope(wakeRecipient, stored.ProjectId, stored.RunId);
            wakeRecipient.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, stored.RecipientSessionId);
            await wakeRecipient.ExecuteNonQueryAsync(cancellationToken);
        }

        var notificationKind = receipt.Purpose switch
        {
            AddressedMessagePurpose.Handoff => "handoff",
            AddressedMessagePurpose.NeedsInput => "needs_input",
            AddressedMessagePurpose.Error => "error",
            _ => null
        };
        if (notificationKind is not null)
        {
            await using var notification = new NpgsqlCommand($"""
                INSERT INTO {_notifications}
                    (project_id, run_id, notification_id, parent_session_id, child_session_id,
                     message_id, notification_kind, wakes_parent)
                SELECT @project, @run, @notification, recipient.session_id, sender.session_id,
                    @message, @kind, @wakes
                FROM {_sessions} AS sender
                JOIN {_sessions} AS recipient
                  ON recipient.project_id = sender.project_id AND recipient.run_id = sender.run_id
                 AND recipient.session_id = sender.parent_session_id
                WHERE sender.project_id = @project AND sender.run_id = @run
                    AND sender.session_id = @sender AND recipient.session_id = @recipient
                ON CONFLICT (project_id, message_id) DO NOTHING
                """, connection, transaction);
            AddRunScope(notification, stored.ProjectId, stored.RunId);
            notification.Parameters.AddWithValue("notification", NpgsqlDbType.Uuid, Guid.NewGuid());
            notification.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, request.OwnerMessageId);
            notification.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, notificationKind);
            notification.Parameters.AddWithValue("wakes", NpgsqlDbType.Boolean, wakes);
            notification.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, stored.SenderSessionId);
            notification.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, stored.RecipientSessionId);
            await notification.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Reads an already completed turn-boundary operation for an idempotent retry.
    /// </summary>
    public async Task<TurnBoundaryResult?> ReadCompletedTurnBoundaryAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        long executionFence,
        long requestStateVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (executionFence < 1 || requestStateVersion < 1)
            throw new CoordinationException("turn_fence_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        if (session.ExecutionFence != executionFence || run.Fence != executionFence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        if (session.TurnBoundaryRequestVersion != requestStateVersion ||
            session.TurnBoundaryResult is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var result = JsonSerializer.Deserialize<TurnBoundaryResult>(session.TurnBoundaryResult, JsonOptions);
        var runtimeTurnId = CreateRuntimeTurnId(identity, session.ExecutionFence, session.LogicalTurnOrdinal);
        if (result is null || result.Session != identity || result.ExecutionState != "active" ||
            (!string.IsNullOrEmpty(result.RuntimeTurnId) && result.RuntimeTurnId != runtimeTurnId))
            throw new InvalidOperationException("A persisted turn-boundary result is invalid.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result with { RuntimeTurnId = runtimeTurnId };
    }

    /// <summary>
    /// Advances a session to its next logical turn under state-version and execution-fence checks.
    /// </summary>
    /// <remarks>The transaction records duplicate-safe operation state and presents at most one pending message.</remarks>
    public async Task<TurnBoundaryResult> AdvanceTurnBoundaryAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        long expectedFence,
        long expectedStateVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (expectedFence < 1 || expectedStateVersion < 1)
            throw new CoordinationException("turn_fence_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        await RequireNoCheckpointRecoveryHoldAsync(
            connection, transaction, identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false);
        if (session.ExecutionFence != expectedFence || expectedFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        // A retry resumes the reserved boundary without allocating another logical turn.
        var resumedBoundary = session.TurnState == "presenting" &&
            session.TurnBoundaryRequestVersion == expectedStateVersion &&
            session.StateVersion > expectedStateVersion;
        var canStartBoundary = session.StateVersion == expectedStateVersion &&
            (session.TurnState == "idle" || (session.TurnState == "blocked" && session.PendingWake));
        if (!resumedBoundary && !canStartBoundary)
            throw new CoordinationException("turn_boundary_conflict", StatusCodes.Status409Conflict);

        TurnBoundaryResult result;
        if (resumedBoundary)
        {
            result = new TurnBoundaryResult(
                identity, session.LogicalTurnOrdinal, session.StateVersion,
                "presenting",
                session.PendingWake,
                null,
                RuntimeTurnId: CreateRuntimeTurnId(identity, expectedFence, session.LogicalTurnOrdinal));
        }
        else
        {
            await using var update = new NpgsqlCommand($"""
                UPDATE {_sessions}
                SET logical_turn_ordinal = logical_turn_ordinal + 1,
                    turn_state = 'presenting',
                    state_version = state_version + 1,
                    turn_boundary_request_version = @version,
                    turn_boundary_result = NULL
                WHERE project_id = @project AND run_id = @run AND session_id = @session
                    AND execution_fence = @fence AND state_version = @version
                    AND (turn_state = 'idle' OR (turn_state = 'blocked' AND pending_wake))
                    AND lifecycle_state = 'active'
                RETURNING logical_turn_ordinal, state_version, pending_wake
                """, connection, transaction);
            AddRunScope(update, identity.ProjectId, identity.RunId);
            update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, expectedFence);
            update.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, expectedStateVersion);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new CoordinationException("turn_boundary_conflict", StatusCodes.Status409Conflict);
            result = new TurnBoundaryResult(
                identity,
                reader.GetInt64(0),
                reader.GetInt64(1),
                "presenting",
                reader.GetBoolean(2),
                null,
                RuntimeTurnId: CreateRuntimeTurnId(identity, expectedFence, reader.GetInt64(0)));
            await reader.DisposeAsync();
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Completes the current turn boundary and applies the requested idle, blocked, or completed state.
    /// </summary>
    public async Task<TurnBoundaryResult> CompleteTurnBoundaryAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        long executionFence,
        long requestStateVersion,
        long presentingStateVersion,
        AddressedMessageEnvelope? presentedMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        await RequireNoCheckpointRecoveryHoldAsync(
            connection, transaction, identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false);
        if (session.ExecutionFence != executionFence || executionFence != run.Fence ||
            session.TurnState != "presenting" ||
            session.TurnBoundaryRequestVersion != requestStateVersion ||
            presentingStateVersion <= requestStateVersion ||
            session.StateVersion < presentingStateVersion)
            throw new CoordinationException("turn_boundary_conflict", StatusCodes.Status409Conflict);

        // Clear the original wake only if nothing changed while message presentation was in progress.
        var pendingWake = session.StateVersion == requestStateVersion + 1
            ? false
            : session.PendingWake;
        var stateVersion = checked(session.StateVersion + 1);
        var notifications = await ReadParentNotificationsAsync(
            connection, transaction, identity, 50, cancellationToken).ConfigureAwait(false);
        var result = new TurnBoundaryResult(
            identity,
            session.LogicalTurnOrdinal,
            stateVersion,
            "active",
            pendingWake,
            presentedMessage,
            notifications.ToImmutableArray(),
            CreateRuntimeTurnId(identity, executionFence, session.LogicalTurnOrdinal));
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        await using var update = new NpgsqlCommand($"""
            UPDATE {_sessions}
            SET turn_state = 'active',
                state_version = @nextVersion,
                pending_wake = @pendingWake,
                turn_boundary_result = @result
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND execution_fence = @fence AND state_version = @version
                AND turn_state = 'presenting'
                AND turn_boundary_request_version = @requestVersion
                AND lifecycle_state = 'active'
            """, connection, transaction);
        AddRunScope(update, identity.ProjectId, identity.RunId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
        update.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, session.StateVersion);
        update.Parameters.AddWithValue("nextVersion", NpgsqlDbType.Bigint, stateVersion);
        update.Parameters.AddWithValue("requestVersion", NpgsqlDbType.Bigint, requestStateVersion);
        update.Parameters.AddWithValue("pendingWake", NpgsqlDbType.Boolean, pendingWake);
        update.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, resultJson);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException("turn_boundary_conflict", StatusCodes.Status409Conflict);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Records a failed or indeterminate run transition and advances the fence to stop stale sessions.
    /// </summary>
    /// <remarks>Expected run and session versions plus an idempotency key make the transition conflict-safe and retryable.</remarks>
    public async Task<RunExecutionTransitionResult> ReportRunFailureAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        ReportRunFailureRequest request,
        CancellationToken cancellationToken,
        AuthorizedRunSelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRunFailureRequest(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireRunOwner(run, actor);

        var requestHash = HashRunExecutionOperation("failure", identity, request);
        var replay = await ReadRunExecutionOperationAsync(
            connection,
            transaction,
            identity.ProjectId,
            identity.RunId,
            actor,
            "failure",
            request.IdempotencyKey,
            requestHash,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }

        await RequireNoCheckpointRecoveryHoldAsync(
            connection, transaction, identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false);
        if (request.ExecutionFence != run.Fence ||
            request.ExpectedRunStateVersion != run.StateVersion ||
            session.ExecutionFence != run.Fence ||
            request.ExpectedSessionStateVersion != session.StateVersion)
            throw new CoordinationException("execution_failure_stale", StatusCodes.Status409Conflict);
        if (run.ExecutionState is "failed" or "indeterminate" or "completed" or "cancelled" ||
            session.LifecycleState != "active" ||
            session.TurnState is not ("active" or "presenting") ||
            await IsLegacyCompletedRunAsync(
                connection, transaction, identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("execution_failure_conflict", StatusCodes.Status409Conflict);

        var previousFence = run.Fence;
        var expectedSessionCount = await ReadRunSessionCountAtFenceAsync(
            connection, transaction, identity.ProjectId, identity.RunId, previousFence, cancellationToken)
            .ConfigureAwait(false);
        var nextFence = checked(previousFence + 1);
        var failedState = request.State == OwnerRunFailureState.Failed ? "failed" : "indeterminate";
        var operationId = Guid.NewGuid();
        var fencedSessions = ImmutableArray.CreateBuilder<SessionExecutionFenceChange>();
        var targetStateVersion = 0L;
        // Other in-flight turns have unknown outcomes; fencing them does not undo their side effects.
        await using (var updateSessions = new NpgsqlCommand($"""
            UPDATE {_sessions}
            SET execution_fence = @nextFence,
                turn_state = CASE
                    WHEN session_id = @source THEN @failureState
                    WHEN turn_state IN ('presenting', 'active') THEN 'indeterminate'
                    ELSE turn_state END,
                state_version = state_version + 1,
                turn_boundary_request_version = CASE
                    WHEN turn_state IN ('presenting', 'active') THEN NULL
                    ELSE turn_boundary_request_version END,
                turn_boundary_result = CASE
                    WHEN turn_state IN ('presenting', 'active') THEN NULL
                    ELSE turn_boundary_result END
            WHERE project_id = @project AND run_id = @run AND execution_fence = @previousFence
            RETURNING session_id, turn_state, state_version
            """, connection, transaction))
        {
            AddRunScope(updateSessions, identity.ProjectId, identity.RunId);
            updateSessions.Parameters.AddWithValue("source", NpgsqlDbType.Varchar, identity.SessionId);
            updateSessions.Parameters.AddWithValue("previousFence", NpgsqlDbType.Bigint, previousFence);
            updateSessions.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, nextFence);
            updateSessions.Parameters.AddWithValue("failureState", NpgsqlDbType.Varchar, failedState);
            await using var reader = await updateSessions.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sessionId = reader.GetString(0);
                var state = reader.GetString(1);
                var stateVersion = reader.GetInt64(2);
                fencedSessions.Add(new SessionExecutionFenceChange(sessionId, state, stateVersion));
                if (sessionId == identity.SessionId)
                    targetStateVersion = stateVersion;
            }
        }
        if (targetStateVersion == 0 || fencedSessions.Count != expectedSessionCount)
            throw new CoordinationException("execution_fence_inconsistent", StatusCodes.Status503ServiceUnavailable);

        long runStateVersion;
        await using (var updateRun = new NpgsqlCommand($"""
            UPDATE {_runs}
            SET execution_fence = @nextFence,
                logical_turn_ordinal = @turnOrdinal,
                execution_state = @failureState,
                execution_cause_code = @cause,
                execution_reference = @reference,
                completed_at = clock_timestamp(),
                state_version = state_version + 1,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run
                AND execution_fence = @previousFence AND state_version = @runVersion
            RETURNING state_version
            """, connection, transaction))
        {
            AddRunScope(updateRun, identity.ProjectId, identity.RunId);
            updateRun.Parameters.AddWithValue("previousFence", NpgsqlDbType.Bigint, previousFence);
            updateRun.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, nextFence);
            updateRun.Parameters.AddWithValue("turnOrdinal", NpgsqlDbType.Bigint, session.LogicalTurnOrdinal);
            updateRun.Parameters.AddWithValue("failureState", NpgsqlDbType.Varchar, failedState);
            updateRun.Parameters.AddWithValue("cause", NpgsqlDbType.Varchar, request.CauseCode);
            updateRun.Parameters.AddWithValue("reference", NpgsqlDbType.Varchar, request.Reference);
            updateRun.Parameters.AddWithValue("runVersion", NpgsqlDbType.Bigint, run.StateVersion);
            var value = await updateRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null)
                throw new CoordinationException("execution_failure_stale", StatusCodes.Status409Conflict);
            runStateVersion = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        await AdvanceCurrentDecisionFenceAsync(
            connection,
            transaction,
            actor,
            new SessionIdentity(identity.ProjectId, identity.RunId, session.RootSessionId),
            selection,
            previousFence,
            nextFence,
            operationId,
            request.State == OwnerRunFailureState.Failed
                ? "execution.failed"
                : "execution.indeterminate",
            cancellationToken).ConfigureAwait(false);

        var result = new RunExecutionTransitionResult(
            operationId,
            identity,
            run.ExecutionState,
            failedState,
            previousFence,
            nextFence,
            session.LogicalTurnOrdinal,
            session.StateVersion,
            targetStateVersion,
            run.StateVersion,
            runStateVersion,
            request.CauseCode,
            request.Reference,
            null,
            null,
            fencedSessions.ToImmutable());
        await SaveRunExecutionOperationAsync(
            connection, transaction, actor, "failure", request.IdempotencyKey, requestHash, result,
            cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(new
        {
            result,
            runtimeEffectsState = "unavailable",
            runtimeEffectsUnavailableCode = "runtime_effect_owner_unavailable"
        }, JsonOptions);
        var eventType = failedState == "failed"
            ? "orchestrator.run.execution_failed"
            : "orchestrator.run.execution_indeterminate";
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            operationId,
            $"coordination/{identity.ProjectId}/{identity.RunId}/execution",
            $"failure:{actor.Issuer}:{actor.Subject}:{request.IdempotencyKey}",
            eventType,
            EventVersion,
            payload,
            _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Recovers an eligible run and advances its execution fence so previous session work cannot continue.
    /// </summary>
    public async Task<RunExecutionTransitionResult> RecoverRunExecutionAsync(
        CoordinationActor actor,
        string projectId,
        string runId,
        RecoverRunExecutionRequest request,
        CancellationToken cancellationToken,
        AuthorizedRunSelection? selection = null,
        MafExecutionCheckpointStore? checkpointStore = null,
        CoordinatorGateRequest? pendingGate = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRunRecoveryRequest(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, projectId, runId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        RequireRunOwner(run, actor);
        var root = await ReadRootSessionAsync(
            connection, transaction, projectId, runId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        if (root is null)
            throw new CoordinationException("run_root_unavailable", StatusCodes.Status503ServiceUnavailable);
        RequireWriter(root, actor);

        var identity = new SessionIdentity(projectId, runId, root.SessionId);
        var requestHash = HashRunExecutionOperation("recovery", identity, request);
        var replay = await ReadRunExecutionOperationAsync(
            connection,
            transaction,
            projectId,
            runId,
            actor,
            "recovery",
            request.IdempotencyKey,
            requestHash,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            replay = await ApplyCheckpointRecoverySupersessionOverlayAsync(
                connection, transaction, replay, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }

        if (request.CheckpointRecovery is null)
            await RequireNoCheckpointRecoveryHoldAsync(
                connection, transaction, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (request.ExecutionFence != run.Fence || request.ExpectedRunStateVersion != run.StateVersion ||
            root.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_recovery_stale", StatusCodes.Status409Conflict);
        MafExecutionCheckpointSnapshot? sourceCheckpoint = null;
        EncodedChildRecoveryManifest? childManifest = null;
        var childBoundary = false;
        var childRecoveryIndeterminate = false;
        var operationId = Guid.NewGuid();
        var recoveryGate = pendingGate;
        var checkpointRecovery = request.CheckpointRecovery;
        ChildRecoveryRootStartup? rootStartup = null;
        CheckpointRecoverySupersessionReceipt? supersessionReceipt = null;
        if (checkpointRecovery is null)
        {
            if (run.ExecutionState is not ("failed" or "indeterminate") ||
                run.ExecutionCauseCode is null || run.ExecutionReference is null)
                throw new CoordinationException("execution_recovery_conflict", StatusCodes.Status409Conflict);
        }
        else
        {
            if (run.ExecutionState is not ("idle" or "active" or "blocked" or "failed" or "indeterminate") ||
                selection is null || checkpointStore is null ||
                !checkpointStore.IsBoundTo(identity, run.Fence))
                throw new CoordinationException(
                    "checkpoint_recovery_unavailable", StatusCodes.Status409Conflict);
            await LockLatestRecoveryDecisionAsync(
                connection, transaction, identity, cancellationToken).ConfigureAwait(false);
            var hasRegisteredChild = await HasRegisteredChildWorkAsync(
                connection, transaction, projectId, runId, cancellationToken).ConfigureAwait(false);
            var children = hasRegisteredChild
                ? await ChildCheckpointRecoveryCensus.LockChildrenAsync(
                    connection, transaction, _schema, identity, cancellationToken).ConfigureAwait(false)
                : ImmutableArray<ChildRecoverySession>.Empty;
            await checkpointStore.AcquireExecutionLockInTransactionAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            sourceCheckpoint = await checkpointStore.ReadLatestInTransactionAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            if (sourceCheckpoint is null ||
                sourceCheckpoint.Info.CheckpointId != checkpointRecovery.SourceCheckpointId ||
                sourceCheckpoint.State.Revision != checkpointRecovery.SourceCheckpointRevision ||
                sourceCheckpoint.State.DecisionStateVersion != checkpointRecovery.ExpectedDecisionStateVersion)
                throw new CoordinationException(
                    "checkpoint_recovery_checkpoint_stale", StatusCodes.Status409Conflict);
            var recoveryHeld = await CheckpointRecoveryHold.IsHeldAsync(
                    connection, transaction, _schema, projectId, runId, cancellationToken)
                .ConfigureAwait(false);
            var originManifest = recoveryHeld
                ? await ReadCheckpointRecoveryOriginManifestAsync(
                    connection, transaction, identity, run.Fence, sourceCheckpoint, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            RequireNoChildJoinPlatformHistory(sourceCheckpoint.State, hasRegisteredChild);
            if (hasRegisteredChild)
            {
                if (sourceCheckpoint.State.PendingDispatches.Count == 0 &&
                    sourceCheckpoint.State.FixedWorkAssociations.Count == 0 &&
                    sourceCheckpoint.State.Results.Count == 0 &&
                    sourceCheckpoint.State.ChildTurnReceipts.Count == 0 &&
                    !sourceCheckpoint.State.Progress.WorkItems.Values
                        .Concat(sourceCheckpoint.State.Progress.FixedWorkItems.Values)
                        .Any(status => status != MafExecutionTaskStatus.Pending))
                    throw new CoordinationException(
                        "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict);
                if (_decisionStore is null)
                    throw new CoordinationException(
                        "checkpoint_recovery_decision_unavailable", StatusCodes.Status409Conflict);
                var decision = await _decisionStore.ReadConfirmedWorkPlanForRecoveryInTransactionAsync(
                    connection, transaction, actor, identity, selection,
                    checkpointRecovery.ExpectedDecisionStateVersion, run.Fence,
                    sourceCheckpoint.State.WorkPlanId, cancellationToken).ConfigureAwait(false);
                recoveryGate = decision.PendingGate;
                if (recoveryGate?.RequestId != pendingGate?.RequestId ||
                    recoveryGate is not null &&
                    (recoveryGate.Fence != run.Fence || recoveryGate.AuthorizedActorId != actor.Subject))
                    throw new CoordinationException(
                        "checkpoint_recovery_gate_unavailable", StatusCodes.Status409Conflict);
                rootStartup = await RequireCheckpointRecoveryRuntimeAsync(
                    connection, transaction, actor, run.TenantId, identity, run.Fence,
                    checkpointRecovery, cancellationToken, requireChildEvidence: true).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict);
                var manifest = await ChildCheckpointRecoveryCensus.BuildAsync(
                    connection, transaction, _schema, actor, identity, run.TenantId, run.SelectionHash,
                    run.Fence, operationId, checkpointRecovery, rootStartup, sourceCheckpoint,
                    decision.ConfirmedWorkPlan!, children, cancellationToken, originManifest)
                    .ConfigureAwait(false);
                childManifest = ChildCheckpointRecoveryManifestCodec.Encode(manifest);
                childBoundary = true;
                childRecoveryIndeterminate = manifest.OtherNativeWork.Length > 0 ||
                    manifest.OutputCaptures.Any(capture => capture.State != "admitted") ||
                    manifest.Members.Any(member => member.NativeOrigin?.State is
                        "sending" or "terminal_pending_accounting" or "indeterminate");
            }
            else
            {
                RequireGateCheckpointBoundary(sourceCheckpoint.State);
                if (pendingGate is null || pendingGate.Fence != run.Fence ||
                    pendingGate.AuthorizedActorId != actor.Subject)
                    throw new CoordinationException(
                        "checkpoint_recovery_gate_unavailable", StatusCodes.Status409Conflict);
                if (await CoordinationRuntimeRunStartStore.HasUnresolvedNativeWorkAsync(
                        connection, transaction, _schema, projectId, runId, cancellationToken)
                    .ConfigureAwait(false))
                    throw new CoordinationException(
                        "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict);
            }

            if (rootStartup is null)
                _ = await RequireCheckpointRecoveryRuntimeAsync(
                    connection, transaction, actor, run.TenantId, identity, run.Fence,
                    checkpointRecovery, cancellationToken).ConfigureAwait(false);
            if (recoveryHeld)
            {
                if (!childBoundary || childManifest is null || sourceCheckpoint is null)
                    throw new CoordinationException(
                        "checkpoint_recovery_dispatch_held", StatusCodes.Status409Conflict);
                var successorManifest = ChildCheckpointRecoveryManifestCodec.Decode(
                    childManifest.Json, childManifest.Sha256);
                var superseded = await ReadCheckpointRecoverySupersessionSourceAsync(
                    connection, transaction, actor, run.TenantId, identity, run.Fence,
                    run.SelectionHash, sourceCheckpoint, successorManifest, cancellationToken).ConfigureAwait(false);
                supersessionReceipt = CheckpointRecoverySupersessionReceipt.Create(
                    run.TenantId,
                    actor,
                    run.SelectionHash,
                    successorManifest,
                    superseded,
                    operationId,
                    requestHash,
                    sourceCheckpoint);
            }
            await RequireNoPendingCheckpointRecoveryAsync(
                connection, transaction, projectId, runId, cancellationToken).ConfigureAwait(false);
        }
        if (await CoordinationRuntimeRunStartStore.HasTerminalCleanupMarkerAsync(
                connection, transaction, _schema, run.TenantId, projectId, runId,
                run.ExecutionState, run.Fence, cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("runtime_start_cleanup_recovery_blocked", StatusCodes.Status409Conflict);

        var previousFence = run.Fence;
        var expectedSessionCount = await ReadRunSessionCountAtFenceAsync(
            connection, transaction, projectId, runId, previousFence, cancellationToken)
            .ConfigureAwait(false);
        var nextFence = checked(previousFence + 1);
        var fencedSessions = ImmutableArray.CreateBuilder<SessionExecutionFenceChange>();
        var rootStateVersion = 0L;
        // Recovery restores owner state at a new fence; it does not replay physical runtime effects.
        await using (var updateSessions = new NpgsqlCommand($"""
            UPDATE {_sessions}
            SET execution_fence = @nextFence,
                turn_state = CASE
                    WHEN NOT @childBoundary AND turn_state IN ('failed', 'indeterminate') THEN 'idle'
                    ELSE turn_state END,
                state_version = state_version + 1,
                turn_boundary_request_version = CASE
                    WHEN NOT @childBoundary AND turn_state IN ('failed', 'indeterminate') THEN NULL
                    ELSE turn_boundary_request_version END,
                turn_boundary_result = CASE
                    WHEN NOT @childBoundary AND turn_state IN ('failed', 'indeterminate') THEN NULL
                    ELSE turn_boundary_result END
            WHERE project_id = @project AND run_id = @run AND execution_fence = @previousFence
            RETURNING session_id, turn_state, state_version
            """, connection, transaction))
        {
            AddRunScope(updateSessions, projectId, runId);
            updateSessions.Parameters.AddWithValue("previousFence", NpgsqlDbType.Bigint, previousFence);
            updateSessions.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, nextFence);
            updateSessions.Parameters.AddWithValue("childBoundary", NpgsqlDbType.Boolean, childBoundary);
            await using var reader = await updateSessions.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sessionId = reader.GetString(0);
                var state = reader.GetString(1);
                var stateVersion = reader.GetInt64(2);
                fencedSessions.Add(new SessionExecutionFenceChange(sessionId, state, stateVersion));
                if (sessionId == root.SessionId)
                    rootStateVersion = stateVersion;
            }
        }
        if (rootStateVersion == 0 || fencedSessions.Count != expectedSessionCount)
            throw new CoordinationException("execution_fence_inconsistent", StatusCodes.Status503ServiceUnavailable);

        long runStateVersion;
        await using (var updateRun = new NpgsqlCommand($"""
            UPDATE {_runs}
            SET execution_fence = @nextFence,
                execution_state = CASE
                    WHEN @childBoundary THEN 'blocked'
                    WHEN @checkpointRecovery THEN 'active'
                    ELSE 'idle' END,
                execution_cause_code = NULL,
                execution_reference = NULL,
                completed_at = NULL,
                state_version = state_version + 1,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run
                AND execution_fence = @previousFence AND state_version = @runVersion
                AND (
                    (@checkpointRecovery AND execution_state IN ('idle', 'active', 'blocked', 'failed', 'indeterminate')) OR
                    (NOT @checkpointRecovery AND execution_state IN ('failed', 'indeterminate'))
                )
            RETURNING state_version
            """, connection, transaction))
        {
            AddRunScope(updateRun, projectId, runId);
            updateRun.Parameters.AddWithValue("previousFence", NpgsqlDbType.Bigint, previousFence);
            updateRun.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, nextFence);
            updateRun.Parameters.AddWithValue("runVersion", NpgsqlDbType.Bigint, run.StateVersion);
            updateRun.Parameters.AddWithValue(
                "checkpointRecovery", NpgsqlDbType.Boolean, checkpointRecovery is not null);
            updateRun.Parameters.AddWithValue("childBoundary", NpgsqlDbType.Boolean, childBoundary);
            var value = await updateRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null)
                throw new CoordinationException("execution_recovery_stale", StatusCodes.Status409Conflict);
            runStateVersion = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        await AdvanceCurrentDecisionFenceAsync(
            connection,
            transaction,
            actor,
            identity,
            selection,
            previousFence,
            nextFence,
            operationId,
            checkpointRecovery is null ? "execution.recovered" : "execution.checkpoint_recovery",
            cancellationToken,
            checkpointRecovery is not null,
            checkpointRecovery?.ExpectedDecisionStateVersion,
            sourceCheckpoint?.State.WorkPlanId).ConfigureAwait(false);

        MafExecutionCheckpointSnapshot? recoveryCheckpoint = null;
        long? recoveryDecisionStateVersion = null;
        if (supersessionReceipt is not null)
            _ = await new RuntimeExecutionEvidenceLedgerStore(_schema[1..^1])
                .AppendCheckpointRecoverySupersessionAsync(
                    connection, transaction, supersessionReceipt, cancellationToken).ConfigureAwait(false);
        if (checkpointRecovery is not null)
        {
            recoveryDecisionStateVersion = await ReadDecisionStateVersionAtFenceAsync(
                connection, transaction, identity, nextFence, cancellationToken).ConfigureAwait(false);
            recoveryCheckpoint = await checkpointStore!.AppendRecoveryCheckpointInTransactionAsync(
                connection, transaction, sourceCheckpoint!, operationId,
                recoveryDecisionStateVersion.Value, cancellationToken, supersessionReceipt)
                .ConfigureAwait(false);
        }

        var result = new RunExecutionTransitionResult(
            operationId,
            identity,
            run.ExecutionState,
            checkpointRecovery is null ? "idle" : childBoundary ? "blocked" : "active",
            previousFence,
            nextFence,
            root.LogicalTurnOrdinal,
            root.StateVersion,
            rootStateVersion,
            run.StateVersion,
            runStateVersion,
            request.CauseCode,
            request.Reference,
            run.ExecutionCauseCode,
            run.ExecutionReference,
            fencedSessions.ToImmutable())
        {
            CheckpointRecovery = checkpointRecovery is null
                ? null
                : new CheckpointRecoveryStatus(
                "pending",
                    sourceCheckpoint!.Info.CheckpointId,
                    sourceCheckpoint.State.Revision,
                    previousFence,
                    nextFence,
                    checkpointRecovery.RuntimeInstanceId,
                    checkpointRecovery.RegistrationRevision)
                {
                    CurrentStep = childBoundary ? "child-recovery-unavailable" : "retirement",
                    BoundaryKind = childBoundary ? "childJoin" : null,
                    DispatchHeld = childBoundary ? true : null,
                    ChildManifestSha256 = childManifest?.Sha256,
                    ReasonCode = childBoundary
                        ? childRecoveryIndeterminate
                            ? "checkpoint_recovery_work_unresolved"
                            : "checkpoint_recovery_child_continuation_unavailable"
                        : null,
                    SourceCheckpointHash = sourceCheckpoint!.PayloadHash,
                    RecoveryCheckpointId = recoveryCheckpoint!.Info.CheckpointId,
                    RecoveryCheckpointRevision = recoveryCheckpoint.State.Revision,
                    RecoveryCheckpointHash = recoveryCheckpoint.PayloadHash,
                    DecisionStateVersion = recoveryDecisionStateVersion!.Value,
                    SupersedesOperationId = supersessionReceipt?.SupersedesOperationId,
                    SupersessionReceiptId = supersessionReceipt?.ReceiptId,
                    SupersessionPayloadHash = supersessionReceipt?.CanonicalPayloadHash,
                    GateRequestId = recoveryGate?.RequestId,
                    GateAuthorizedActorId = recoveryGate?.AuthorizedActorId
                }
        };
        await SaveRunExecutionOperationAsync(
            connection, transaction, actor, "recovery", request.IdempotencyKey, requestHash, result,
            cancellationToken, childManifest).ConfigureAwait(false);
        var payload = checkpointRecovery is null
            ? JsonSerializer.SerializeToElement(new
            {
                result,
                runtimeEffectsState = "unavailable",
                runtimeEffectsUnavailableCode = "runtime_effect_owner_unavailable",
                physicalEffectsReplayed = false
            }, JsonOptions)
            : JsonSerializer.SerializeToElement(new
            {
                result,
                runtimeEffectsState = childBoundary
                    ? childRecoveryIndeterminate ? "indeterminate" : "blocked"
                    : "pending",
                physicalEffectsReplayed = false
            }, JsonOptions);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            operationId,
            $"coordination/{projectId}/{runId}/execution",
            $"recovery:{actor.Issuer}:{actor.Subject}:{request.IdempotencyKey}",
            checkpointRecovery is null
                ? "orchestrator.run.execution_recovered"
                : childBoundary
                    ? childRecoveryIndeterminate
                        ? "orchestrator.run.checkpoint_recovery_indeterminate"
                        : "orchestrator.run.checkpoint_recovery_blocked"
                    : "orchestrator.run.checkpoint_recovery_pending",
            EventVersion,
            payload,
            _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task LockLatestRecoveryDecisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT decision_id
            FROM {_decisions}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            ORDER BY state_version DESC
            LIMIT 1
            FOR SHARE
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new CoordinationException(
                "checkpoint_recovery_decision_unavailable", StatusCodes.Status409Conflict);
    }

    private async Task<bool> HasRegisteredChildWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT session_id
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run AND node_kind = 'child_work'
            ORDER BY session_id
            LIMIT 1
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task RequireNoCheckpointRecoveryHoldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        if (await CheckpointRecoveryHold.IsHeldAsync(
                connection, transaction, _schema, projectId, runId, cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "checkpoint_recovery_dispatch_held", StatusCodes.Status409Conflict);
    }

    internal static void RequireNoChildJoinPlatformHistory(
        MafExecutionCheckpoint checkpoint, bool hasRegisteredChild = false)
    {
        if (!hasRegisteredChild &&
            checkpoint.PendingDispatches.Count == 0 && checkpoint.FixedWorkAssociations.Count == 0 &&
            checkpoint.Results.Count == 0 && checkpoint.ChildTurnReceipts.Count == 0 &&
            !checkpoint.Progress.WorkItems.Values.Concat(checkpoint.Progress.FixedWorkItems.Values)
                .Any(status => status != MafExecutionTaskStatus.Pending))
            return;

        var intent = checkpoint.BuildTestIntents.OrderBy(item => item.Key, StringComparer.Ordinal).FirstOrDefault();
        if (intent.Value is not null)
            throw new CoordinationException(
                "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict)
            {
                Reason = "build_test_history_requires_effect_recovery",
                StepId = intent.Key,
                OperationId = intent.Value.OperationId
            };
        var receipt = checkpoint.BuildTestReceipts.OrderBy(item => item.Key, StringComparer.Ordinal).FirstOrDefault();
        if (receipt.Value is not null)
            throw new CoordinationException(
                "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict)
            {
                Reason = "build_test_history_requires_effect_recovery",
                StepId = receipt.Key,
                OperationId = receipt.Value.OperationId
            };
        if (checkpoint.ScribeIntents.Count > 0 || checkpoint.ScribeReceipts.Count > 0 ||
            checkpoint.OpenPullRequestIntents.Count > 0 || checkpoint.OpenPullRequestReceipts.Count > 0 ||
            checkpoint.MergeIntents.Count > 0 || checkpoint.MergeReceipts.Count > 0 ||
            checkpoint.ReviewIntents.Count > 0 || checkpoint.ReviewReceipts.Count > 0 ||
            checkpoint.RubberduckIntents.Count > 0 || checkpoint.RubberduckReceipts.Count > 0 ||
            checkpoint.RubberduckFailureCodes.Count > 0)
            throw new CoordinationException(
                "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict);
    }

    internal static void RequireGateCheckpointBoundary(MafExecutionCheckpoint checkpoint)
    {
        var unresolvedProgress = checkpoint.Progress.WorkItems.Values
                .Concat(checkpoint.Progress.NonModelSteps.Values)
                .Concat(checkpoint.Progress.FixedWorkItems.Values)
                .Any(status => status is MafExecutionTaskStatus.Running or MafExecutionTaskStatus.Failed or
                    MafExecutionTaskStatus.Indeterminate);
        var completedChildWork = checkpoint.Progress.WorkItems.Values
            .Concat(checkpoint.Progress.FixedWorkItems.Values)
            .Any(status => status == MafExecutionTaskStatus.Succeeded);
        if (unresolvedProgress || completedChildWork || checkpoint.PendingDispatches.Count > 0 ||
            checkpoint.FixedWorkAssociations.Count > 0 ||
            checkpoint.Results.Count > 0 || checkpoint.ChildTurnReceipts.Count > 0 ||
            checkpoint.ScribeIntents.Count > 0 || checkpoint.BuildTestIntents.Count > 0 ||
            checkpoint.OpenPullRequestIntents.Count > 0 || checkpoint.MergeIntents.Count > 0 ||
            checkpoint.ReviewIntents.Count > 0 || checkpoint.RubberduckIntents.Count > 0)
            throw new CoordinationException(
                "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict);
    }

    private async Task<long> ReadDecisionStateVersionAtFenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        long executionFence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT state_version
            FROM {_schema}.coordinator_decisions
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND execution_fence = @fence
            ORDER BY state_version DESC
            LIMIT 1
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null
            ? throw new CoordinationException(
                "checkpoint_recovery_decision_unavailable", StatusCodes.Status409Conflict)
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<ChildRecoveryRootStartup?> RequireCheckpointRecoveryRuntimeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        string tenantId,
        SessionIdentity identity,
        long executionFence,
        CheckpointRecoveryRequest request,
        CancellationToken cancellationToken,
        bool requireChildEvidence = false)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT state, accepted_selection_hash, execution_fence, runtime_instance_id,
                   (receipt_links -> 'registration' ->> 'revision')::bigint,
                   intent_id, bootstrap_operation_id, consume_operation_id, exchange_operation_id,
                   current_step, environment_id, row_version, receipt_links, cleanup_obligation
            FROM {_schema}.runtime_run_start_intents
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
                AND root_session_id = @root
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, identity.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != "ready" ||
            reader.GetString(1).Trim() != request.AcceptedSelectionHash ||
            reader.GetInt64(2) != executionFence ||
            reader.IsDBNull(3) || reader.GetGuid(3) != request.RuntimeInstanceId ||
            reader.IsDBNull(4) || reader.GetInt64(4) != request.RegistrationRevision ||
            requireChildEvidence && reader.GetBoolean(13))
            throw new CoordinationException(
                "checkpoint_recovery_runtime_stale", StatusCodes.Status409Conflict);
        if (!requireChildEvidence)
            return null;
        var intentId = reader.GetGuid(5);
        var bootstrapId = reader.GetGuid(6);
        var consumeId = reader.GetGuid(7);
        var exchangeId = reader.GetGuid(8);
        var state = reader.GetString(0);
        var step = reader.GetString(9);
        var environmentId = reader.IsDBNull(10) ? null : reader.GetString(10);
        var runtimeId = reader.GetGuid(3);
        var registrationRevision = reader.GetInt64(4);
        var rowVersion = reader.GetInt64(11);
        using var links = JsonDocument.Parse(reader.GetString(12));
        ChildCheckpointRecoveryCensus.ValidateReceiptLinks(links.RootElement);
        await reader.DisposeAsync().ConfigureAwait(false);
        var registration = ChildCheckpointRecoveryCensus.ReadReceipt<RuntimeRegistration>(
            links.RootElement, "registration");
        var placement = ChildCheckpointRecoveryCensus.ReadReceipt<RuntimeRunStartPlacementReceipt>(
            links.RootElement, "placement");
        var bootstrap = ChildCheckpointRecoveryCensus.ReadReceipt<RuntimeBootstrapDeliveryReceipt>(
            links.RootElement, "bootstrap");
        if (registration is null || placement is null || bootstrap is null)
            throw new CoordinationException(
                "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict);
        try
        {
            placement.Validate();
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict, exception);
        }
        var binding = registration.Binding;
        if (registration.RuntimeInstanceId != runtimeId || registration.Revision != registrationRevision ||
            registration.State != RuntimeRegistrationState.Active ||
            binding.ActorIssuer != actor.Issuer || binding.ActorId != actor.Subject ||
            binding.TenantId != tenantId || binding.ProjectId != identity.ProjectId ||
            binding.RunId != identity.RunId || binding.SessionId != identity.SessionId ||
            binding.ExecutionFence != executionFence ||
            !string.Equals(binding.AcceptedSelectionHash, request.AcceptedSelectionHash, StringComparison.OrdinalIgnoreCase) ||
            binding.EnvironmentId != environmentId || binding.EnvironmentId != placement.EnvironmentId ||
            binding.ProfileId != placement.SourceProfileId ||
            binding.PlacementUid != placement.SandboxResource.ResourceId ||
            binding.PlacementGeneration != placement.SandboxResource.Generation ||
            binding.PlacementProviderId != placement.SandboxResource.ProviderId ||
            binding.EnvironmentLifecycleGeneration != placement.LifecycleGeneration ||
            binding.EnvironmentLeaseRevision != placement.LeaseRevision ||
            binding.EnvironmentCurrentFencingGeneration != placement.CurrentFencingGeneration ||
            binding.EnvironmentProviderFencingGeneration != placement.ProviderFencingGeneration ||
            string.IsNullOrWhiteSpace(placement.PodUid) ||
            bootstrap.OperationId != bootstrapId || bootstrap.RuntimeInstanceId != runtimeId ||
            bootstrap.RegistrationRevision != registrationRevision ||
            bootstrap.PlacementUid != binding.PlacementUid ||
            bootstrap.PlacementGeneration != binding.PlacementGeneration ||
            bootstrap.ExecutionFence != executionFence ||
            bootstrap.EnvironmentCurrentFencingGeneration != binding.EnvironmentCurrentFencingGeneration ||
            bootstrap.EnvironmentProviderFencingGeneration != binding.EnvironmentProviderFencingGeneration)
            throw new CoordinationException(
                "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict);
        var (currentRevision, bindingHash) = await ChildCheckpointRecoveryCensus.ReadRegistrationAsync(
            connection, transaction, _schema, registration, cancellationToken).ConfigureAwait(false);
        if (currentRevision != registrationRevision || bindingHash is null)
            throw new CoordinationException(
                "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict);
        var priorStarts = await ChildCheckpointRecoveryCensus.ReadPriorStartupsAsync(
            connection, transaction, _schema, links.RootElement, binding, cancellationToken).ConfigureAwait(false);
        return new ChildRecoveryRootStartup(
            intentId, bootstrapId, consumeId, exchangeId, state, step, environmentId,
            runtimeId, registrationRevision, rowVersion,
            ChildCheckpointRecoveryManifestCodec.Hash(links.RootElement), bindingHash, placement,
            ChildCheckpointRecoveryCensus.HashReceipt(links.RootElement.GetProperty("bootstrap")),
            bootstrap.GrantId, bootstrap.ConfigurationHash, priorStarts);
    }

    internal async Task RequireCheckpointRecoveryRegistrationAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        long executionFence,
        CheckpointRecoveryRegistrationAuthorization authorization,
        CancellationToken cancellationToken)
    {
        if (authorization.OperationId == Guid.Empty ||
            authorization.Status.Phase is not ("pending" or "ready") ||
            authorization.Status.PreviousExecutionFence == long.MaxValue ||
            authorization.Status.PreviousExecutionFence + 1 != executionFence ||
            authorization.Status.ExecutionFence != executionFence ||
            authorization.Status.PreviousRuntimeInstanceId == Guid.Empty ||
            authorization.Status.PreviousRegistrationRevision < 1 ||
            authorization.Status.DecisionStateVersion < 1)
            throw new CoordinationException(
                "checkpoint_recovery_registration_stale", StatusCodes.Status409Conflict);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT result::text, progress::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND actor_issuer = @issuer AND actor_subject = @subject
                AND operation_id = @operation AND operation_kind = 'recovery'
            FOR SHARE
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, authorization.OperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var stored = !await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? null
            : JsonSerializer.Deserialize<RunExecutionTransitionResult>(reader.GetString(0), JsonOptions);
        var progress = stored is null
            ? default
            : JsonSerializer.Deserialize<JsonElement>(reader.GetString(1));
        if (stored?.Session != identity ||
            stored.OperationId != authorization.OperationId ||
            stored.ExecutionFence != executionFence ||
            stored.CheckpointRecovery != authorization.Status ||
            authorization.Member is null && identity.SessionId != stored.Session.SessionId ||
            authorization.Member is { } memberBinding &&
                (stored.CheckpointRecovery?.BoundaryKind != "childJoin" ||
                 ChildCheckpointRecoveryManifestCodec.RequireMember(
                     ReadCheckpointRecoveryManifest(stored, progress), memberBinding).Session.SessionId !=
                     memberBinding.SessionId))
            throw new CoordinationException(
                "checkpoint_recovery_registration_stale", StatusCodes.Status409Conflict);
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CheckpointRecoveryRegistrationAuthorization>
        ReadCheckpointRecoveryRegistrationAuthorizationAsync(
            CoordinationActor actor,
            string projectId,
            string runId,
            string sessionId,
            Guid operationId,
            CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT result::text, progress::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND actor_issuer = @issuer
                AND actor_subject = @subject AND operation_id = @operation
                AND operation_kind = 'recovery'
            """, connection);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "checkpoint_recovery_registration_stale", StatusCodes.Status409Conflict);
        var result = JsonSerializer.Deserialize<RunExecutionTransitionResult>(
            reader.GetString(0), JsonOptions);
        var progress = JsonSerializer.Deserialize<JsonElement>(reader.GetString(1));
        if (result?.CheckpointRecovery is not { Phase: "pending" or "ready" } status ||
            result.Session.ProjectId != projectId || result.Session.RunId != runId ||
            result.OperationId != operationId)
            throw new CoordinationException(
                "checkpoint_recovery_registration_stale", StatusCodes.Status409Conflict);
        if (result.Session.SessionId == sessionId)
            return new CheckpointRecoveryRegistrationAuthorization(operationId, status);
        if (status.BoundaryKind != "childJoin")
            throw new CoordinationException(
                "checkpoint_recovery_registration_stale", StatusCodes.Status409Conflict);
        var manifest = ReadCheckpointRecoveryManifest(result, progress);
        var matches = manifest.Members.Where(member => member.Session.SessionId == sessionId).ToArray();
        if (matches.Length != 1)
            throw new CoordinationException(
                "checkpoint_recovery_registration_stale", StatusCodes.Status409Conflict);
        var memberBinding = new CheckpointRecoveryRegistrationMember(
            matches[0].AssociationId,
            matches[0].Session.SessionId,
            matches[0].AssociationHash,
            manifest.SourceExecutionFence,
            manifest.SourceDecisionStateVersion,
            matches[0].Startup.RegistrationId ?? Guid.Empty,
            matches[0].Startup.RegistrationRevision ?? 0);
        _ = ChildCheckpointRecoveryManifestCodec.RequireMember(manifest, memberBinding);
        return new CheckpointRecoveryRegistrationAuthorization(operationId, status, memberBinding);
    }

    internal async Task<ChildCheckpointRecoveryManifest> ReadCheckpointRecoveryManifestAsync(
        CoordinationActor actor,
        SessionIdentity root,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT result::text, progress::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND actor_issuer = @issuer AND actor_subject = @subject
                AND operation_id = @operation AND operation_kind = 'recovery'
            """, connection);
        AddRunScope(command, root.ProjectId, root.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, root.SessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "checkpoint_recovery_manifest_invalid", StatusCodes.Status409Conflict);
        var result = JsonSerializer.Deserialize<RunExecutionTransitionResult>(
            reader.GetString(0), JsonOptions);
        var progress = JsonSerializer.Deserialize<JsonElement>(reader.GetString(1));
        if (result?.Session != root || result.OperationId != operationId ||
            result.CheckpointRecovery is not { Phase: "pending" })
            throw new CoordinationException(
                "checkpoint_recovery_manifest_invalid", StatusCodes.Status409Conflict);
        return ReadCheckpointRecoveryManifest(result, progress);
    }

    internal async Task<CheckpointRecoveryStatus> ReadCheckpointRecoveryStatusAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT result::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND actor_issuer = @issuer AND actor_subject = @subject
                AND operation_id = @operation AND operation_kind = 'recovery'
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
        var storedJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        var stored = storedJson is null
            ? null
            : JsonSerializer.Deserialize<RunExecutionTransitionResult>(storedJson, JsonOptions);
        if (stored?.Session != identity || stored.OperationId != operationId ||
            stored.CheckpointRecovery is not { } status)
            throw new CoordinationException(
                "checkpoint_recovery_registration_stale", StatusCodes.Status409Conflict);
        stored = await ApplyCheckpointRecoverySupersessionOverlayAsync(
            connection, transaction, stored, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored.CheckpointRecovery!;
    }

    private async Task RequireNoPendingCheckpointRecoveryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT 1
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run
                AND operation_kind = 'recovery'
                AND result -> 'checkpointRecovery' ->> 'phase' = 'pending'
            LIMIT 1
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            throw new CoordinationException(
                "checkpoint_recovery_in_progress", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Advances the coordinator decision fence in the same transaction as the run transition.
    /// </summary>
    private Task AdvanceCurrentDecisionFenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity root,
        AuthorizedRunSelection? selection,
        long previousFence,
        long nextFence,
        Guid operationId,
        string actionKind,
        CancellationToken cancellationToken,
        bool preservePendingGate = false,
        long? expectedDecisionStateVersion = null,
        string? expectedCheckpointWorkPlanId = null)
    {
        if (_decisionStore is null)
        {
            if (selection is not null)
                throw new InvalidOperationException("The coordinator decision owner is not configured.");
            return Task.CompletedTask;
        }

        ArgumentNullException.ThrowIfNull(selection);
        return _decisionStore.AdvanceExecutionFenceAsync(
            connection,
            transaction,
            actor,
            root,
            selection,
            previousFence,
            nextFence,
            operationId,
            actionKind,
            cancellationToken,
            preservePendingGate,
            expectedDecisionStateVersion,
            expectedCheckpointWorkPlanId);
    }

    /// <summary>
    /// Finishes a session turn and creates any required parent idle notifications in the owner transaction.
    /// </summary>
    public async Task<TurnBoundaryResult> FinishTurnAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        FinishTurnRequest request,
        CancellationToken cancellationToken,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? afterFinish = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExecutionFence < 1 || request.ExpectedStateVersion < 1 ||
            !Enum.IsDefined(request.Completion))
            throw new CoordinationException("turn_completion_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        await RequireNoCheckpointRecoveryHoldAsync(
            connection, transaction, identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false);
        if (session.ExecutionFence != request.ExecutionFence || request.ExecutionFence != run.Fence ||
            session.StateVersion != request.ExpectedStateVersion || session.TurnState != "active")
            throw new CoordinationException("turn_completion_conflict", StatusCodes.Status409Conflict);

        var nextState = request.Completion switch
        {
            LogicalTurnCompletion.Idle => "idle",
            LogicalTurnCompletion.Blocked => "blocked",
            LogicalTurnCompletion.Completed => "completed",
            _ => throw new CoordinationException("turn_completion_invalid", StatusCodes.Status400BadRequest)
        };
        await using var update = new NpgsqlCommand($"""
            UPDATE {_sessions}
            SET turn_state = @state,
                lifecycle_state = CASE WHEN @state = 'completed' THEN 'completed' ELSE lifecycle_state END,
                state_version = state_version + 1
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND execution_fence = @fence AND state_version = @version AND turn_state = 'active'
            RETURNING logical_turn_ordinal, state_version, pending_wake
            """, connection, transaction);
        AddRunScope(update, identity.ProjectId, identity.RunId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, request.ExecutionFence);
        update.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, request.ExpectedStateVersion);
        update.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, nextState);
        await using var reader = await update.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new CoordinationException("turn_completion_conflict", StatusCodes.Status409Conflict);
        var result = new TurnBoundaryResult(
            identity,
            reader.GetInt64(0),
            reader.GetInt64(1),
            nextState,
            reader.GetBoolean(2),
            null,
            ParentNotifications: ImmutableArray<ParentNotification>.Empty,
            RuntimeTurnId: CreateRuntimeTurnId(identity, request.ExecutionFence, reader.GetInt64(0)));
        await reader.DisposeAsync();
        if (request.Completion == LogicalTurnCompletion.Completed)
            await MarkRunCompletedWhenRootAsync(connection, transaction, identity, cancellationToken)
                .ConfigureAwait(false);
        if (request.Completion == LogicalTurnCompletion.Idle)
            await EnqueueIdleNotificationsAsync(
                connection,
                transaction,
                identity,
                request.ExecutionFence,
                result.LogicalTurnOrdinal,
                result.StateVersion,
                cancellationToken).ConfigureAwait(false);
        else if (request.Completion == LogicalTurnCompletion.Completed && afterFinish is not null)
            await afterFinish(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Records the terminal run outcome when the completed session is the run's root session.
    /// </summary>
    /// <remarks>
    /// The caller holds the run row lock. Completion keeps the current execution fence.
    /// </remarks>
    private async Task MarkRunCompletedWhenRootAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {_runs} AS r
            SET completed_at = clock_timestamp(),
                execution_state = 'completed',
                state_version = state_version + 1,
                updated_at = clock_timestamp()
            WHERE r.project_id = @project AND r.run_id = @run AND r.completed_at IS NULL
                AND r.execution_state IN ('idle', 'active', 'blocked')
                AND EXISTS (
                    SELECT 1 FROM {_sessions} AS s
                    WHERE s.project_id = r.project_id AND s.run_id = r.run_id
                        AND s.session_id = @session AND s.parent_session_id IS NULL)
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            await CoordinationRuntimeRunStartStore.CaptureCleanupInTransactionAsync(
                connection, transaction, _schema, identity.ProjectId, identity.RunId, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates idempotent notifications for subscribers whose target session has just become idle.
    /// </summary>
    private async Task EnqueueIdleNotificationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity target,
        long executionFence,
        long logicalTurnOrdinal,
        long stateVersion,
        CancellationToken cancellationToken)
    {
        var subscriptions = new List<IdleSubscriptionRow>();
        await using (var query = new NpgsqlCommand($"""
            SELECT subscription_id, subscriber_session_id, mode
            FROM {_schema}.coordination_idle_subscriptions
            WHERE project_id = @project AND run_id = @run
                AND target_session_id = @target AND execution_fence = @fence
                AND active AND last_notified_turn < @turn
            ORDER BY subscription_id
            FOR UPDATE
            """, connection, transaction))
        {
            AddRunScope(query, target.ProjectId, target.RunId);
            query.Parameters.AddWithValue("target", NpgsqlDbType.Varchar, target.SessionId);
            query.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
            query.Parameters.AddWithValue("turn", NpgsqlDbType.Bigint, logicalTurnOrdinal);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                subscriptions.Add(new IdleSubscriptionRow(
                    reader.GetGuid(0), reader.GetString(1), ParseIdleMode(reader.GetString(2))));
        }

        foreach (var subscription in subscriptions)
        {
            var notificationId = Guid.NewGuid();
            var subscriber = new SessionIdentity(target.ProjectId, target.RunId, subscription.SubscriberSessionId);
            var notification = new CoordinationIdleNotification(
                notificationId, target, subscriber, executionFence, logicalTurnOrdinal, stateVersion);
            await using (var insert = new NpgsqlCommand($"""
                INSERT INTO {_schema}.coordination_idle_notifications
                    (notification_id, project_id, run_id, subscription_id, turn_ordinal)
                VALUES (@id, @project, @run, @subscription, @turn)
                """, connection, transaction))
            {
                AddRunScope(insert, target.ProjectId, target.RunId);
                insert.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, notificationId);
                insert.Parameters.AddWithValue("subscription", NpgsqlDbType.Uuid, subscription.SubscriptionId);
                insert.Parameters.AddWithValue("turn", NpgsqlDbType.Bigint, logicalTurnOrdinal);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var update = new NpgsqlCommand($"""
                UPDATE {_schema}.coordination_idle_subscriptions
                SET last_notified_turn = @turn,
                    active = CASE WHEN mode = 'once' THEN false ELSE active END
                WHERE project_id = @project AND run_id = @run
                    AND subscription_id = @subscription AND execution_fence = @fence
                    AND active AND last_notified_turn < @turn
                """, connection, transaction))
            {
                AddRunScope(update, target.ProjectId, target.RunId);
                update.Parameters.AddWithValue("subscription", NpgsqlDbType.Uuid, subscription.SubscriptionId);
                update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
                update.Parameters.AddWithValue("turn", NpgsqlDbType.Bigint, logicalTurnOrdinal);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new CoordinationException(
                        "idle_subscription_transition_conflict", StatusCodes.Status409Conflict);
            }

            var payload = JsonSerializer.SerializeToElement(notification, JsonOptions);
            await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
                notificationId,
                $"coordination/{subscriber.ProjectId}/{subscriber.RunId}/{subscriber.SessionId}",
                $"{subscription.SubscriptionId:D}:idle:{logicalTurnOrdinal}",
                "orchestrator.session.idle_notification_requested",
                EventVersion,
                payload,
                _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records that a message was presented to its addressed session at a particular turn boundary.
    /// </summary>
    public async Task RecordPresentationAsync(
        CoordinationActor actor,
        SessionIdentity recipient,
        AddressedMessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Status != AddressedMessageStatus.Delivered ||
            envelope.Recipient != recipient || envelope.MessageId == Guid.Empty)
            throw new CoordinationException("presentation_receipt_invalid", StatusCodes.Status409Conflict);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, recipient.ProjectId, recipient.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, recipient, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        await RequireNoCheckpointRecoveryHoldAsync(
            connection, transaction, recipient.ProjectId, recipient.RunId, cancellationToken).ConfigureAwait(false);
        if (session.ExecutionFence != envelope.RecipientFence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        await using var update = new NpgsqlCommand($"""
            UPDATE {_messages}
            SET status = 'presented'
            WHERE project_id = @project AND run_id = @run AND events_message_id = @eventsMessage
                AND recipient_session_id = @session AND recipient_fence = @fence
                AND status = 'admitted'
            """, connection, transaction);
        AddRunScope(update, recipient.ProjectId, recipient.RunId);
        update.Parameters.AddWithValue("eventsMessage", NpgsqlDbType.Uuid, envelope.MessageId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, recipient.SessionId);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, envelope.RecipientFence);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
        {
            await using var verify = new NpgsqlCommand($"""
                SELECT count(*) FROM {_messages}
                WHERE project_id = @project AND run_id = @run AND events_message_id = @eventsMessage
                    AND recipient_session_id = @session AND recipient_fence = @fence
                    AND status IN ('presented', 'acknowledged')
                """, connection, transaction);
            AddRunScope(verify, recipient.ProjectId, recipient.RunId);
            verify.Parameters.AddWithValue("eventsMessage", NpgsqlDbType.Uuid, envelope.MessageId);
            verify.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, recipient.SessionId);
            verify.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, envelope.RecipientFence);
            if ((long)(await verify.ExecuteScalarAsync(cancellationToken) ?? 0L) != 1)
                throw new CoordinationException("presentation_receipt_mismatch", StatusCodes.Status409Conflict);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Records a recipient's acknowledgment of a previously presented owner message.
    /// </summary>
    public async Task RecordAcknowledgementAsync(
        CoordinationActor actor,
        SessionIdentity recipient,
        AddressedMessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Status != AddressedMessageStatus.Acknowledged ||
            envelope.Recipient != recipient || envelope.MessageId == Guid.Empty)
            throw new CoordinationException("acknowledgment_receipt_invalid", StatusCodes.Status409Conflict);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, recipient.ProjectId, recipient.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, recipient, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        await RequireNoCheckpointRecoveryHoldAsync(
            connection, transaction, recipient.ProjectId, recipient.RunId, cancellationToken).ConfigureAwait(false);
        if (session.ExecutionFence != envelope.RecipientFence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        await using var update = new NpgsqlCommand($"""
            UPDATE {_messages}
            SET status = 'acknowledged'
            WHERE project_id = @project AND run_id = @run AND events_message_id = @eventsMessage
                AND recipient_session_id = @session AND recipient_fence = @fence
                AND status IN ('admitted', 'presented')
            """, connection, transaction);
        AddRunScope(update, recipient.ProjectId, recipient.RunId);
        update.Parameters.AddWithValue("eventsMessage", NpgsqlDbType.Uuid, envelope.MessageId);
        update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, recipient.SessionId);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, envelope.RecipientFence);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
        {
            await using var verify = new NpgsqlCommand($"""
                SELECT count(*) FROM {_messages}
                WHERE project_id = @project AND run_id = @run AND events_message_id = @eventsMessage
                    AND recipient_session_id = @session AND recipient_fence = @fence
                    AND status = 'acknowledged'
                """, connection, transaction);
            AddRunScope(verify, recipient.ProjectId, recipient.RunId);
            verify.Parameters.AddWithValue("eventsMessage", NpgsqlDbType.Uuid, envelope.MessageId);
            verify.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, recipient.SessionId);
            verify.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, envelope.RecipientFence);
            if ((long)(await verify.ExecuteScalarAsync(cancellationToken) ?? 0L) != 1)
                throw new CoordinationException("acknowledgment_receipt_mismatch", StatusCodes.Status409Conflict);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Reads pending notifications addressed to the authenticated parent session.
    /// </summary>
    public async Task<IReadOnlyList<ParentNotification>> ReadParentNotificationsAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (limit is < 1 or > 100)
            throw new CoordinationException("notification_limit_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, parent.ProjectId, parent.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, parent, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        if (run.ExecutionState == "cancelled")
            throw new CoordinationException("run_terminal", StatusCodes.Status409Conflict);

        var notifications = await ReadParentNotificationsAsync(
            connection, transaction, parent, limit, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return notifications;
    }

    /// <summary>
    /// Reads pending parent notifications through the supplied owner transaction.
    /// </summary>
    private async Task<ImmutableArray<ParentNotification>> ReadParentNotificationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity parent,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT notification_id, parent_session_id, child_session_id, message_id,
                notification_kind, wakes_parent, created_at
            FROM {_notifications}
            WHERE project_id = @project AND run_id = @run AND parent_session_id = @parent
                AND acknowledged_at IS NULL
            ORDER BY created_at, notification_id
            LIMIT @limit
            """, connection, transaction);
        AddRunScope(command, parent.ProjectId, parent.RunId);
        command.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var notifications = ImmutableArray.CreateBuilder<ParentNotification>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var purpose = reader.GetString(4) switch
            {
                "handoff" => AddressedMessagePurpose.Handoff,
                "needs_input" => AddressedMessagePurpose.NeedsInput,
                "error" => AddressedMessagePurpose.Error,
                _ => throw new InvalidOperationException("A parent notification kind is invalid.")
            };
            notifications.Add(new ParentNotification(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                purpose,
                reader.GetBoolean(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return notifications.ToImmutable();
    }

    /// <summary>
    /// Acknowledges a parent notification only for its addressed parent session.
    /// </summary>
    public async Task AcknowledgeParentNotificationAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (notificationId == Guid.Empty)
            throw new CoordinationException("notification_id_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, parent.ProjectId, parent.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(
            connection, transaction, parent, forUpdate: true, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        await using var update = new NpgsqlCommand($"""
            UPDATE {_notifications}
            SET acknowledged_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND notification_id = @notification
                AND parent_session_id = @parent AND acknowledged_at IS NULL
            """, connection, transaction);
        AddRunScope(update, parent.ProjectId, parent.RunId);
        update.Parameters.AddWithValue("notification", NpgsqlDbType.Uuid, notificationId);
        update.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
        {
            await using var verify = new NpgsqlCommand($"""
                SELECT count(*) FROM {_notifications}
                WHERE project_id = @project AND run_id = @run AND notification_id = @notification
                    AND parent_session_id = @parent AND acknowledged_at IS NOT NULL
                """, connection, transaction);
            AddRunScope(verify, parent.ProjectId, parent.RunId);
            verify.Parameters.AddWithValue("notification", NpgsqlDbType.Uuid, notificationId);
            verify.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, parent.SessionId);
            if ((long)(await verify.ExecuteScalarAsync(cancellationToken) ?? 0L) != 1)
                throw new CoordinationException("notification_not_found", StatusCodes.Status404NotFound);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Loads a prior run transition by operation identity and request hash.
    /// </summary>
    private async Task<RunExecutionTransitionResult?> ReadRunExecutionOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        CoordinationActor actor,
        string operationKind,
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT operation_kind, request_hash, result
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run
                AND actor_issuer = @issuer AND actor_subject = @subject
                AND idempotency_key = @key
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.GetString(0) != operationKind || reader.GetString(1).TrimEnd() != requestHash)
            throw new CoordinationException("execution_operation_idempotency_conflict", StatusCodes.Status409Conflict);
        var result = JsonSerializer.Deserialize<RunExecutionTransitionResult>(
            reader.GetString(2), JsonOptions);
        if (result is null)
            throw new CoordinationException(
                "execution_operation_result_unavailable", StatusCodes.Status503ServiceUnavailable);
        return result with { IsDuplicate = true };
    }

    /// <summary>
    /// Stores a run transition result for safe idempotent retries.
    /// </summary>
    private async Task SaveRunExecutionOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        string operationKind,
        string idempotencyKey,
        string requestHash,
        RunExecutionTransitionResult result,
        CancellationToken cancellationToken,
        EncodedChildRecoveryManifest? childManifest = null)
    {
        if (childManifest is null && result.CheckpointRecovery?.BoundaryKind == "childJoin")
            throw new CoordinationException(
                "checkpoint_recovery_manifest_invalid", StatusCodes.Status409Conflict);
        if (childManifest is not null)
        {
            var manifest = ChildCheckpointRecoveryManifestCodec.Decode(childManifest.Json, childManifest.Sha256);
            if (operationKind != "recovery" ||
                result.CheckpointRecovery is not { BoundaryKind: "childJoin" } admission ||
                manifest.OperationId != result.OperationId || manifest.Root != result.Session ||
                manifest.ActorIssuer != actor.Issuer || manifest.ActorId != actor.Subject ||
                manifest.HoldFence != result.ExecutionFence ||
                manifest.SourceExecutionFence != result.PreviousExecutionFence ||
                manifest.SourceCheckpointId != admission.SourceCheckpointId ||
                manifest.SourceCheckpointRevision != admission.SourceCheckpointRevision ||
                manifest.SourceCheckpointHash != admission.SourceCheckpointHash)
                throw new CoordinationException(
                    "checkpoint_recovery_manifest_invalid", StatusCodes.Status409Conflict);
        }
        var resultJson = JsonSerializer.SerializeToElement(result, JsonOptions);
        var progressJson = childManifest is not null
            ? JsonSerializer.SerializeToElement(new
            {
                boundaryKind = "childJoin",
                dispatchHeld = result.CheckpointRecovery!.Phase != "ready",
                holdFence = result.ExecutionFence,
                childManifest = childManifest.Json,
                childManifestSha256 = childManifest.Sha256,
                result.CheckpointRecovery!.Phase,
                result.CheckpointRecovery.CurrentStep,
                result.CheckpointRecovery.ReasonCode,
                result.CheckpointRecovery.RecoveryMaterialMode
            }, JsonOptions)
            : result.CheckpointRecovery is { } recovery
                ? JsonSerializer.SerializeToElement(new
            {
                recovery.Phase,
                recovery.CurrentStep,
                recovery.ReasonCode,
                recovery.RecoveryMaterialMode
            }, JsonOptions)
                : JsonSerializer.SerializeToElement(new { }, JsonOptions);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_executionOperations}
                (project_id, run_id, operation_id, session_id, actor_issuer, actor_subject,
                 operation_kind, idempotency_key, request_hash, result, progress)
            VALUES (@project, @run, @operation, @session, @issuer, @subject,
                @kind, @key, @hash, @result, @progress)
            """, connection, transaction);
        AddRunScope(command, result.Session.ProjectId, result.Session.RunId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, result.OperationId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, result.Session.SessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, operationKind);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Char, requestHash);
        command.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, resultJson.GetRawText());
        command.Parameters.AddWithValue("progress", NpgsqlDbType.Jsonb, progressJson.GetRawText());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RunExecutionTransitionResult> UpdateCheckpointRecoveryProgressAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        Guid operationId,
        CheckpointRecoveryStatus status,
        CancellationToken cancellationToken,
        RuntimeRunStartIntent? readyIntent = null,
        MafExecutionCheckpointStore? checkpointStore = null,
        RuntimeUsageSourceStore? usageSources = null,
        ImmutableDictionary<string, ChildRecoveryCompletedUnjoinedMaterial>? completedUnjoinedMaterials = null,
        ChildCheckpointRecoveryManifest? completedUnjoinedManifest = null)
    {
        var imports = completedUnjoinedMaterials ??
            ImmutableDictionary<string, ChildRecoveryCompletedUnjoinedMaterial>.Empty
                .WithComparers(StringComparer.Ordinal);
        if (imports.Count > 0 &&
            (status.BoundaryKind != "childJoin" || status.Phase != "ready" ||
             checkpointStore is null || usageSources is null || completedUnjoinedManifest is null ||
             completedUnjoinedManifest.OperationId != operationId ||
             completedUnjoinedManifest.Root != identity ||
             completedUnjoinedManifest.HoldFence != status.ExecutionFence ||
             completedUnjoinedManifest.SourceExecutionFence != status.PreviousExecutionFence ||
             !string.Equals(
                 ChildCheckpointRecoveryManifestCodec.Hash(completedUnjoinedManifest),
                 status.ChildManifestSha256,
                 StringComparison.Ordinal)))
            throw new CoordinationException(
                "checkpoint_recovery_import_invalid", StatusCodes.Status409Conflict);

        if (readyIntent is not null &&
            (status.Phase != "ready" || status.CurrentStep != "ready" || status.ReasonCode is not null ||
             status.RuntimeInstanceId is null || status.RuntimeInstanceId == Guid.Empty ||
             status.RegistrationRevision is not > 0 || readyIntent.ProjectId != identity.ProjectId ||
             readyIntent.RunId != identity.RunId || readyIntent.RootSessionId != identity.SessionId ||
             readyIntent.ExecutionFence != status.ExecutionFence || readyIntent.State != "ready" ||
             readyIntent.CurrentStep != "ready" || readyIntent.FailureCode is not null ||
             readyIntent.RuntimeInstanceId != status.RuntimeInstanceId ||
             string.IsNullOrWhiteSpace(readyIntent.EnvironmentId)))
            throw new ArgumentException("The checkpoint recovery intent update is invalid.", nameof(readyIntent));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        RequireRunOwner(run, actor);
        var root = await ReadRootSessionAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        if (root is null || root.SessionId != identity.SessionId)
            throw new CoordinationException(
                "checkpoint_recovery_progress_conflict", StatusCodes.Status409Conflict);
        RequireWriter(root, actor);

        var importedSessions = new Dictionary<string, SessionRow>(StringComparer.Ordinal);
        MafExecutionCheckpointSnapshot? importCheckpoint = null;
        if (imports.Count > 0)
        {
            await using (var terminalRead = new NpgsqlCommand($"""
                SELECT result
                FROM {_executionOperations}
                WHERE project_id = @project AND run_id = @run AND session_id = @session
                    AND operation_id = @operation AND operation_kind = 'recovery'
                    AND actor_issuer = @issuer AND actor_subject = @subject
                """, connection, transaction))
            {
                AddRunScope(terminalRead, identity.ProjectId, identity.RunId);
                terminalRead.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
                terminalRead.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
                terminalRead.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
                terminalRead.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
                if (await terminalRead.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string stored)
                    throw new CoordinationException(
                        "checkpoint_recovery_operation_unavailable", StatusCodes.Status409Conflict);
                var existing = JsonSerializer.Deserialize<RunExecutionTransitionResult>(stored, JsonOptions)
                    ?? throw new CoordinationException(
                        "execution_operation_result_unavailable", StatusCodes.Status503ServiceUnavailable);
                if (existing.Session != identity || existing.OperationId != operationId)
                    throw new CoordinationException(
                        "checkpoint_recovery_progress_conflict", StatusCodes.Status409Conflict);
                if (existing.CheckpointRecovery is not { Phase: "pending" })
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return existing;
                }
            }

            var members = completedUnjoinedManifest!.Members
                .Where(member => member.Disposition == "completedUnjoined")
                .OrderBy(member => member.Session.SessionId, StringComparer.Ordinal)
                .ToArray();
            if (members.Length != imports.Count ||
                members.Any(member => !imports.ContainsKey(member.AssociationId)) ||
                imports.Keys.Any(associationId =>
                    !members.Any(member => member.AssociationId == associationId)))
                throw new CoordinationException(
                    "checkpoint_recovery_import_invalid", StatusCodes.Status409Conflict);

            foreach (var member in members)
            {
                var child = await ReadSessionAsync(
                    connection, transaction, member.Session is { } saved
                        ? new SessionIdentity(identity.ProjectId, identity.RunId, saved.SessionId)
                        : throw new CoordinationException(
                            "checkpoint_recovery_import_invalid", StatusCodes.Status409Conflict),
                    forUpdate: true, cancellationToken).ConfigureAwait(false);
                RequireWriter(child, actor);
                RequireCurrentActiveSession(child, run);
                if (member.Session.ExecutionFence != status.PreviousExecutionFence ||
                    member.Session.StateVersion == long.MaxValue ||
                    child.ParentSessionId != identity.SessionId ||
                    child.RootSessionId != identity.SessionId ||
                    child.Detached ||
                    child.ExecutionFence != status.ExecutionFence ||
                    child.StateVersion != member.Session.StateVersion + 1 ||
                    child.LifecycleState != "active" ||
                    child.TurnState != "active" ||
                    child.LogicalTurnOrdinal != member.Session.LogicalTurnOrdinal ||
                    child.WorkPlanItemId != member.Session.WorkPlanItemId ||
                    child.ArchivedAt is not null)
                    throw new CoordinationException(
                        "checkpoint_recovery_import_conflict", StatusCodes.Status409Conflict);
                importedSessions.Add(member.AssociationId, child);
            }

            await MafExecutionCheckpointStore.AcquireExecutionLockInTransactionAsync(
                connection, transaction, identity, cancellationToken).ConfigureAwait(false);
            importCheckpoint = await checkpointStore!.ReadLatestInTransactionAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            if (importCheckpoint is null ||
                importCheckpoint.Info.CheckpointId != status.RecoveryCheckpointId ||
                importCheckpoint.State.Revision != status.RecoveryCheckpointRevision ||
                importCheckpoint.State.DecisionStateVersion != status.DecisionStateVersion ||
                importCheckpoint.State.RecoveryLineage is not { } lineage ||
                lineage.SourceCheckpointId != status.SourceCheckpointId ||
                lineage.SourceCheckpointRevision != status.SourceCheckpointRevision ||
                lineage.SourceCheckpointHash != status.SourceCheckpointHash ||
                lineage.RecoveryOperationId != operationId ||
                lineage.RecoveryExecutionFence != status.ExecutionFence)
                throw new CoordinationException(
                    "checkpoint_recovery_checkpoint_stale", StatusCodes.Status409Conflict);
        }

        await using var read = new NpgsqlCommand($"""
            SELECT phase_version, result, progress
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation AND operation_kind = 'recovery'
                AND actor_issuer = @issuer AND actor_subject = @subject
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(read, identity.ProjectId, identity.RunId);
        read.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        read.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
        read.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        read.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "checkpoint_recovery_operation_unavailable", StatusCodes.Status409Conflict);
        var phaseVersion = reader.GetInt64(0);
        var result = JsonSerializer.Deserialize<RunExecutionTransitionResult>(
            reader.GetString(1), JsonOptions)
            ?? throw new CoordinationException(
                "execution_operation_result_unavailable", StatusCodes.Status503ServiceUnavailable);
        var storedProgress = JsonSerializer.Deserialize<JsonElement>(reader.GetString(2));
        await reader.DisposeAsync().ConfigureAwait(false);
        if (result.Session != identity || result.OperationId != operationId)
            throw new CoordinationException(
                "checkpoint_recovery_progress_conflict", StatusCodes.Status409Conflict);
        if (result.CheckpointRecovery is not { Phase: "pending" } current)
            return result;
        RequireCurrentCheckpointRecoveryOwner(
            result, current, run.Fence, run.ExecutionState, root.ExecutionFence, root.LifecycleState);
        ChildCheckpointRecoveryManifest? childManifest = null;
        if (current.BoundaryKind == "childJoin")
        {
            childManifest = ReadCheckpointRecoveryManifest(result, storedProgress);
            if (imports.Count > 0 &&
                !string.Equals(
                    ChildCheckpointRecoveryManifestCodec.Hash(childManifest),
                    ChildCheckpointRecoveryManifestCodec.Hash(completedUnjoinedManifest!),
                    StringComparison.Ordinal))
                throw new CoordinationException(
                    "checkpoint_recovery_import_invalid", StatusCodes.Status409Conflict);
            if (status.Phase == "ready" &&
                childManifest.Members.Any(member => member.Disposition == "completedUnjoined") &&
                imports.Count == 0)
                throw new CoordinationException(
                    "checkpoint_recovery_import_invalid", StatusCodes.Status409Conflict);
            if (status.Phase == "ready" &&
                (readyIntent is null ||
                 !await AreCheckpointRecoveryMembersReadyAsync(
                     connection, transaction, childManifest, readyIntent, cancellationToken).ConfigureAwait(false)))
                throw new CoordinationException(
                    "checkpoint_recovery_members_not_ready", StatusCodes.Status409Conflict);
        }
        RequireImmutableCheckpointRecoveryAdmission(current, status);
        if (status.Phase == "ready" && readyIntent is null ||
            readyIntent is not null &&
            (readyIntent.TenantId != run.TenantId ||
             !string.Equals(readyIntent.AcceptedSelectionHash, run.SelectionHash, StringComparison.OrdinalIgnoreCase)))
            throw new CoordinationException(
                "checkpoint_recovery_progress_conflict", StatusCodes.Status409Conflict);
        if (current == status)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        if (imports.Count > 0)
        {
            if (childManifest is null || importCheckpoint is null || readyIntent is null ||
                status.Phase != "ready")
                throw new CoordinationException(
                    "checkpoint_recovery_import_invalid", StatusCodes.Status409Conflict);
            await ImportCompletedUnjoinedResultsInTransactionAsync(
                connection,
                transaction,
                identity,
                run,
                operationId,
                status,
                childManifest,
                importCheckpoint,
                importedSessions,
                imports,
                checkpointStore!,
                usageSources!,
                cancellationToken).ConfigureAwait(false);
        }

        if (readyIntent is not null)
        {
            await using var updateIntent = new NpgsqlCommand($"""
                UPDATE {_schema}.runtime_run_start_intents
                SET execution_fence = @nextFence,
                    bootstrap_operation_id = @bootstrap,
                    consume_operation_id = @consume,
                    exchange_operation_id = @exchange,
                    state = 'ready', current_step = 'ready', failure_code = NULL,
                    environment_id = @environment, runtime_instance_id = @runtime,
                    receipt_links = @receipts, row_version = row_version + 1,
                    updated_at = clock_timestamp()
                WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
                    AND root_session_id = @root AND accepted_selection_hash = @selection
                    AND execution_fence = @currentFence AND row_version = @intentVersion
                    AND NOT cleanup_obligation
                """, connection, transaction);
            updateIntent.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, readyIntent.TenantId);
            updateIntent.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, readyIntent.ProjectId);
            updateIntent.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, readyIntent.RunId);
            updateIntent.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, readyIntent.RootSessionId);
            updateIntent.Parameters.AddWithValue("selection", NpgsqlDbType.Char, readyIntent.AcceptedSelectionHash);
            updateIntent.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, readyIntent.ExecutionFence);
            updateIntent.Parameters.AddWithValue("currentFence", NpgsqlDbType.Bigint, status.ExecutionFence);
            updateIntent.Parameters.AddWithValue("intentVersion", NpgsqlDbType.Bigint, readyIntent.RowVersion);
            updateIntent.Parameters.AddWithValue("bootstrap", NpgsqlDbType.Uuid, readyIntent.BootstrapOperationId);
            updateIntent.Parameters.AddWithValue("consume", NpgsqlDbType.Uuid, readyIntent.ConsumeOperationId);
            updateIntent.Parameters.AddWithValue("exchange", NpgsqlDbType.Uuid, readyIntent.ExchangeOperationId);
            updateIntent.Parameters.AddWithValue(
                "environment", NpgsqlDbType.Varchar, (object?)readyIntent.EnvironmentId ?? DBNull.Value);
            updateIntent.Parameters.AddWithValue(
                "runtime", NpgsqlDbType.Uuid, (object?)readyIntent.RuntimeInstanceId ?? DBNull.Value);
            updateIntent.Parameters.AddWithValue("receipts", NpgsqlDbType.Jsonb, readyIntent.ReceiptLinks);
            if (await updateIntent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CoordinationException(
                    "runtime_start_intent_stale", StatusCodes.Status409Conflict);
        }

        var updated = result with { CheckpointRecovery = status };
        var resultJson = JsonSerializer.SerializeToElement(updated, JsonOptions);
        var progress = new Dictionary<string, object?>
        {
            ["phase"] = status.Phase,
            ["currentStep"] = status.CurrentStep,
            ["reasonCode"] = status.ReasonCode,
            ["recoveryMaterialMode"] = status.RecoveryMaterialMode
        };
        if (status.BoundaryKind == "childJoin")
        {
            progress["dispatchHeld"] = status.Phase != "ready";
            if (status.Phase == "ready")
                progress["allMembersReady"] = true;
        }
        var progressJson = JsonSerializer.SerializeToElement(progress, JsonOptions);
        await using (var update = new NpgsqlCommand($"""
            UPDATE {_executionOperations}
            SET result = @result, progress = progress || @progress, phase_version = phase_version + 1
            WHERE project_id = @project AND run_id = @run AND operation_id = @operation
                AND phase_version = @phaseVersion
            """, connection, transaction))
        {
            AddRunScope(update, identity.ProjectId, identity.RunId);
            update.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            update.Parameters.AddWithValue("phaseVersion", NpgsqlDbType.Bigint, phaseVersion);
            update.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, resultJson.GetRawText());
            update.Parameters.AddWithValue("progress", NpgsqlDbType.Jsonb, progressJson.GetRawText());
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CoordinationException(
                    "checkpoint_recovery_progress_conflict", StatusCodes.Status409Conflict);
        }

        var eventType = status.Phase switch
        {
            "ready" => "orchestrator.run.checkpoint_recovery_ready",
            "blocked" => "orchestrator.run.checkpoint_recovery_blocked",
            "indeterminate" => "orchestrator.run.checkpoint_recovery_indeterminate",
            "failed" => "orchestrator.run.checkpoint_recovery_failed",
            _ => "orchestrator.run.checkpoint_recovery_progress"
        };
        var progressEventId = new Guid(SHA256.HashData(
            Encoding.UTF8.GetBytes($"checkpoint-recovery:{operationId:D}:{phaseVersion}"))[..16]);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            progressEventId,
            $"coordination/{identity.ProjectId}/{identity.RunId}/execution",
            $"checkpoint-recovery:{operationId:D}:{phaseVersion}",
            eventType,
            EventVersion,
            JsonSerializer.SerializeToElement(new { operationId, status }, JsonOptions),
            _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task ImportCompletedUnjoinedResultsInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        AcceptedRunRow run,
        Guid operationId,
        CheckpointRecoveryStatus status,
        ChildCheckpointRecoveryManifest manifest,
        MafExecutionCheckpointSnapshot source,
        IReadOnlyDictionary<string, SessionRow> childSessions,
        ImmutableDictionary<string, ChildRecoveryCompletedUnjoinedMaterial> materials,
        MafExecutionCheckpointStore checkpointStore,
        RuntimeUsageSourceStore usageSources,
        CancellationToken cancellationToken)
    {
        var members = manifest.Members
            .Where(member => member.Disposition == "completedUnjoined")
            .OrderBy(member => member.AssociationId, StringComparer.Ordinal)
            .ToArray();
        if (status.Phase != "ready" || status.DispatchHeld != false ||
            status.BoundaryKind != "childJoin" ||
            source.Info.CheckpointId != status.RecoveryCheckpointId ||
            source.State.Revision != status.RecoveryCheckpointRevision ||
            source.State.WorkPlanId != manifest.WorkPlanId ||
            source.State.DecisionStateVersion != status.DecisionStateVersion ||
            source.State.RecoveryLineage is not { } lineage ||
            lineage.RecoveryOperationId != operationId ||
            lineage.RecoveryExecutionFence != status.ExecutionFence ||
            manifest.Root != identity ||
            manifest.SourceExecutionFence != status.PreviousExecutionFence ||
            manifest.HoldFence != status.ExecutionFence ||
            members.Length != materials.Count ||
            members.Any(member => !materials.ContainsKey(member.AssociationId)) ||
            materials.Keys.Any(associationId =>
                !members.Any(member => member.AssociationId == associationId)))
            throw new CoordinationException(
                "checkpoint_recovery_import_invalid", StatusCodes.Status409Conflict);

        var state = source.State;
        var progress = state.Progress;
        var results = state.Results;
        var receipts = state.ChildTurnReceipts;
        foreach (var member in members)
        {
            if (!childSessions.TryGetValue(member.AssociationId, out var child) ||
                !state.PendingDispatches.TryGetValue(member.AssociationId, out var dispatch) ||
                dispatch.AssociationId != member.AssociationId ||
                dispatch.ChildSessionId != member.Session.SessionId ||
                dispatch.MessageId != member.MessageId ||
                dispatch.PromptHash != member.PromptHash ||
                dispatch.EgressReceipt is null ||
                member.EgressReceiptHash is null ||
                ChildCheckpointRecoveryManifestCodec.Hash(dispatch.EgressReceipt) != member.EgressReceiptHash ||
                results.ContainsKey(member.AssociationId) ||
                receipts.ContainsKey(member.AssociationId) ||
                member.Progress != nameof(MafExecutionTaskStatus.Running) ||
                member.Session.ExecutionFence != manifest.SourceExecutionFence ||
                member.Session.Lifecycle != "active" ||
                member.Session.TurnState != "active" ||
                member.Session.ParentSessionId != identity.SessionId ||
                member.Session.RootSessionId != identity.SessionId ||
                member.Session.Detached ||
                member.Startup.Binding.ParentRootSessionId != identity.SessionId ||
                member.Startup.Binding.ChildSessionId != member.Session.SessionId ||
                member.Startup.Binding.WorkPlanId != manifest.WorkPlanId ||
                member.Startup.Binding.AssociationId != member.AssociationId ||
                member.Startup.Binding.DispatchMessageId != member.MessageId ||
                member.Startup.Binding.PromptHash != member.PromptHash ||
                member.Startup.Binding.WorkPlanItemId != member.Session.WorkPlanItemId ||
                child.ExecutionFence != status.ExecutionFence ||
                child.StateVersion != checked(member.Session.StateVersion + 1) ||
                child.TurnState != "active" || child.LifecycleState != "active")
                throw new CoordinationException(
                    "checkpoint_recovery_import_conflict", StatusCodes.Status409Conflict);

            var workItemAssociation = state.FixedWorkAssociations.TryGetValue(
                member.AssociationId, out var fixedAssociation);
            if (workItemAssociation)
            {
                if (member.Startup.Binding.WorkPlanItemId is not null ||
                    ChildCheckpointRecoveryManifestCodec.Hash(fixedAssociation!) != member.AssociationHash ||
                    progress.FixedWorkItems.GetValueOrDefault(member.AssociationId) !=
                        MafExecutionTaskStatus.Running)
                    throw new CoordinationException(
                        "checkpoint_recovery_import_conflict", StatusCodes.Status409Conflict);
                progress = progress with
                {
                    FixedWorkItems = progress.FixedWorkItems.SetItem(
                        member.AssociationId, MafExecutionTaskStatus.Succeeded)
                };
            }
            else
            {
                if (member.Startup.Binding.WorkPlanItemId is null ||
                    member.WorkItemBindingSha256 is null ||
                    progress.WorkItems.GetValueOrDefault(member.AssociationId) !=
                        MafExecutionTaskStatus.Running)
                    throw new CoordinationException(
                        "checkpoint_recovery_import_conflict", StatusCodes.Status409Conflict);
                progress = progress with
                {
                    WorkItems = progress.WorkItems.SetItem(
                        member.AssociationId, MafExecutionTaskStatus.Succeeded)
                };
            }

            var imported = materials[member.AssociationId];
            if (string.IsNullOrEmpty(imported.Prompt) || string.IsNullOrEmpty(imported.Result) ||
                RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(imported.Prompt)) != member.PromptHash ||
                imported.Result.Length > AddressedMessageValidation.MaximumTextLength ||
                imported.Result.Any(character =>
                    char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
                throw new RuntimeAuthorizationException("checkpoint_recovery_result_material_invalid");

            var registration = member.SavedRegistration
                ?? throw new RuntimeAuthorizationException("checkpoint_recovery_import_registration_invalid");
            var evidence = await usageSources.RequireNativeTurnAccountedForRecoveryWithinTransactionAsync(
                connection,
                transaction,
                identity,
                member,
                run.SelectionHash,
                manifest.SourceExecutionFence,
                imported.Result,
                cancellationToken).ConfigureAwait(false);
            var receipt = RuntimeChildTurnCompletionReceipt.CreateForCheckpointRecovery(
                    registration,
                    member.MessageId,
                    RuntimeContractValidation.NativeSessionId(registration.Binding),
                    imported.Prompt,
                    manifest.WorkPlanId,
                    member.AssociationId,
                    member.WorkItemBindingSha256,
                    evidence,
                    imported.UserMaterial,
                    imported.AssistantMaterial)
                .ValidateEvidenceFor(manifest.WorkPlanId, member.AssociationId);
            if (receipt.PromptSha256 != member.PromptHash ||
                receipt.OutputSha256 != RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(imported.Result)) ||
                receipt.ChildSessionId != member.Session.SessionId ||
                receipt.ExecutionFence != manifest.SourceExecutionFence ||
                !string.Equals(
                    receipt.AcceptedSelectionHash, run.SelectionHash, StringComparison.OrdinalIgnoreCase))
                throw new RuntimeAuthorizationException("checkpoint_recovery_import_receipt_invalid");
            receipts = receipts.Add(member.AssociationId, receipt);
            results = results.Add(member.AssociationId, imported.Result);
        }

        foreach (var member in members)
        {
            var child = childSessions[member.AssociationId];
            await using var finish = new NpgsqlCommand($"""
                UPDATE {_sessions}
                SET turn_state = 'completed',
                    lifecycle_state = 'completed',
                    state_version = state_version + 1
                WHERE project_id = @project AND run_id = @run AND session_id = @session
                    AND parent_session_id = @parent AND root_session_id = @root
                    AND NOT detached AND lifecycle_state = 'active' AND turn_state = 'active'
                    AND execution_fence = @fence AND state_version = @version
                """, connection, transaction);
            AddRunScope(finish, identity.ProjectId, identity.RunId);
            finish.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, child.SessionId);
            finish.Parameters.AddWithValue("parent", NpgsqlDbType.Varchar, identity.SessionId);
            finish.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, identity.SessionId);
            finish.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, status.ExecutionFence);
            finish.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, child.StateVersion);
            if (await finish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CoordinationException(
                    "checkpoint_recovery_import_conflict", StatusCodes.Status409Conflict);
        }

        var next = state with
        {
            Revision = checked(state.Revision + 1),
            Progress = progress,
            PendingDispatches = state.PendingDispatches.RemoveRange(
                members.Select(member => member.AssociationId)),
            Results = results,
            ChildTurnReceipts = receipts
        };
        _ = await checkpointStore.AppendCompletedUnjoinedRecoveryImportInTransactionAsync(
            connection,
            transaction,
            Guid.NewGuid().ToString("N"),
            source.Info,
            next,
            cancellationToken).ConfigureAwait(false);
    }

    internal static void RequireCurrentCheckpointRecoveryOwner(
        RunExecutionTransitionResult operation,
        CheckpointRecoveryStatus status,
        long runFence,
        string runState,
        long rootFence,
        string rootLifecycle)
    {
        if (status.PreviousExecutionFence < 1 ||
            status.PreviousExecutionFence == long.MaxValue ||
            status.ExecutionFence != status.PreviousExecutionFence + 1 ||
            operation.PreviousExecutionFence != status.PreviousExecutionFence ||
            operation.ExecutionFence != status.ExecutionFence ||
            runFence != status.ExecutionFence || rootFence != runFence ||
            runState is not ("idle" or "active" or "blocked") || rootLifecycle != "active")
            throw new CoordinationException(
                "checkpoint_recovery_progress_conflict", StatusCodes.Status409Conflict);
    }

    internal static void RequireImmutableCheckpointRecoveryAdmission(
        CheckpointRecoveryStatus current, CheckpointRecoveryStatus next)
    {
        if (next.Phase is not ("pending" or "ready" or "blocked" or "indeterminate" or "failed") ||
            current.SourceCheckpointId != next.SourceCheckpointId ||
            current.SourceCheckpointRevision != next.SourceCheckpointRevision ||
            current.PreviousExecutionFence != next.PreviousExecutionFence ||
            current.ExecutionFence != next.ExecutionFence ||
            current.PreviousRuntimeInstanceId != next.PreviousRuntimeInstanceId ||
            current.PreviousRegistrationRevision != next.PreviousRegistrationRevision ||
            current.BoundaryKind != next.BoundaryKind ||
            current.ChildManifestSha256 != next.ChildManifestSha256 ||
            current.BoundaryKind == "childJoin" &&
                (current.DispatchHeld != true ||
                 next.DispatchHeld != (next.Phase != "ready")) ||
            current.BoundaryKind != "childJoin" &&
                (current.DispatchHeld is not null || next.DispatchHeld is not null) ||
            current.GateRequestId != next.GateRequestId ||
            current.GateAuthorizedActorId != next.GateAuthorizedActorId ||
            current.SourceCheckpointHash != next.SourceCheckpointHash ||
            current.RecoveryCheckpointId != next.RecoveryCheckpointId ||
            current.RecoveryCheckpointRevision != next.RecoveryCheckpointRevision ||
            current.RecoveryCheckpointHash != next.RecoveryCheckpointHash ||
            current.SupersedesOperationId != next.SupersedesOperationId ||
            current.SupersessionReceiptId != next.SupersessionReceiptId ||
            current.SupersessionPayloadHash != next.SupersessionPayloadHash ||
            current.DecisionStateVersion != next.DecisionStateVersion ||
            next.SupersededByOperationId is not null ||
            next.SupersededByReceiptId is not null ||
            next.SupersededByPayloadHash is not null)
            throw new CoordinationException(
                "checkpoint_recovery_progress_conflict", StatusCodes.Status409Conflict);
    }

    private async Task<ChildCheckpointRecoveryManifest> ReadCheckpointRecoveryOriginManifestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        long currentFence,
        MafExecutionCheckpointSnapshot checkpoint,
        CancellationToken cancellationToken)
    {
        var lineage = checkpoint.State.RecoveryLineage;
        if (lineage is null || lineage.RecoveryOperationId == Guid.Empty ||
            lineage.RecoveryExecutionFence != currentFence ||
            currentFence <= 0 || lineage.SourceExecutionFence != currentFence - 1)
            throw new CoordinationException(
                "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);

        await using var command = new NpgsqlCommand($"""
            SELECT result::text, progress::text, phase_version
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND operation_id = @operation
              AND operation_kind = 'recovery'
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, lineage.RecoveryOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);
        var resultJson = reader.GetString(0);
        var progressJson = reader.GetString(1);
        var phaseVersion = reader.GetInt64(2);
        await reader.DisposeAsync().ConfigureAwait(false);

        var operation = JsonSerializer.Deserialize<RunExecutionTransitionResult>(resultJson, JsonOptions);
        using var progress = JsonDocument.Parse(progressJson);
        if (operation is null || operation.OperationId != lineage.RecoveryOperationId ||
            operation.Session != identity || operation.ExecutionFence != currentFence ||
            operation.CheckpointRecovery is not
            {
                BoundaryKind: "childJoin",
                Phase: "blocked",
                DispatchHeld: true
            } status ||
            phaseVersion < 1 ||
            status.ExecutionFence != currentFence ||
            status.RecoveryCheckpointId != checkpoint.Info.CheckpointId ||
            status.RecoveryCheckpointRevision != checkpoint.State.Revision ||
            status.RecoveryCheckpointHash != checkpoint.PayloadHash ||
            lineage.SourceCheckpointId != status.SourceCheckpointId ||
            lineage.SourceCheckpointRevision != status.SourceCheckpointRevision ||
            lineage.SourceCheckpointHash != status.SourceCheckpointHash)
            throw new CoordinationException(
                "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);

        var manifest = ReadCheckpointRecoveryManifest(operation, progress.RootElement);
        if (manifest.HoldFence != currentFence ||
            manifest.SourceExecutionFence != lineage.SourceExecutionFence ||
            manifest.OperationId != lineage.RecoveryOperationId)
            throw new CoordinationException(
                "checkpoint_recovery_supersession_unavailable", StatusCodes.Status409Conflict);
        return manifest;
    }

    private static ChildCheckpointRecoveryManifest ReadCheckpointRecoveryManifest(
        RunExecutionTransitionResult operation,
        JsonElement progress)
    {
        try
        {
            if (!progress.TryGetProperty("childManifest", out var manifest) ||
                manifest.ValueKind != JsonValueKind.String ||
                !progress.TryGetProperty("childManifestSha256", out var digest) ||
                digest.ValueKind != JsonValueKind.String)
                throw new JsonException();
            var value = ChildCheckpointRecoveryManifestCodec.Decode(
                manifest.GetString()!, digest.GetString()!);
            if (operation.CheckpointRecovery is not { BoundaryKind: "childJoin" } status ||
                status.ChildManifestSha256 != digest.GetString() ||
                status.DispatchHeld != true ||
                value.OperationId != operation.OperationId ||
                value.Root != operation.Session ||
                value.HoldFence != operation.ExecutionFence ||
                value.SourceExecutionFence != operation.PreviousExecutionFence ||
                value.SourceCheckpointId != status.SourceCheckpointId ||
                value.SourceCheckpointRevision != status.SourceCheckpointRevision ||
                value.SourceCheckpointHash != status.SourceCheckpointHash)
                throw new JsonException();
            return value;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                           CoordinationException)
        {
            throw new CoordinationException(
                "checkpoint_recovery_manifest_invalid", StatusCodes.Status409Conflict, exception);
        }
    }

    private sealed record CheckpointRecoveryReadyRuntimeRow(
        string SessionId,
        Guid IntentId,
        string State,
        string Step,
        long Fence,
        bool Cleanup,
        string Links,
        long? RegistrationRevision,
        string? RegistrationBindingHash,
        short? RegistrationState,
        DateTimeOffset? RegistrationExpiresAt,
        Guid BootstrapId,
        Guid ConsumeId,
        Guid ExchangeId,
        string? EnvironmentId,
        Guid? RuntimeInstanceId,
        long RowVersion,
        string TenantId,
        string SelectionHash);

    private async Task<bool> AreCheckpointRecoveryMembersReadyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ChildCheckpointRecoveryManifest manifest,
        RuntimeRunStartIntent rootIntent,
        CancellationToken cancellationToken)
    {
        if (rootIntent.State != "ready" || rootIntent.CurrentStep != "ready" ||
            rootIntent.ExecutionFence != manifest.HoldFence || rootIntent.CleanupObligation)
            return false;
        var members = manifest.Members;
        if (members.Any(member => member.Disposition == "unresolved"))
            return false;
        var required = members.Where(member => member.Disposition == "pendingUnsent").ToArray();
        var bySession = new Dictionary<string, CheckpointRecoveryReadyRuntimeRow>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand($"""
            SELECT i.root_session_id, i.intent_id, i.state, i.current_step, i.execution_fence,
                   i.cleanup_obligation, i.receipt_links::text, h.current_revision,
                   r.binding_hash, r.state, r.expires_at, i.bootstrap_operation_id,
                   i.consume_operation_id, i.exchange_operation_id, i.environment_id,
                   i.runtime_instance_id, i.row_version, i.tenant_id, i.accepted_selection_hash
            FROM {_schema}.runtime_run_start_intents i
            LEFT JOIN {_schema}.runtime_registration_heads h
              ON h.runtime_instance_id =
                 NULLIF(i.receipt_links->'registration'->>'runtimeInstanceId', '')::uuid
            LEFT JOIN {_schema}.runtime_registration_revisions r
              ON r.runtime_instance_id = h.runtime_instance_id AND r.revision = h.current_revision
            WHERE i.project_id = @project AND i.run_id = @run
              AND i.root_session_id = ANY(@sessions)
            ORDER BY i.root_session_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, manifest.Root.ProjectId);
            command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, manifest.Root.RunId);
            command.Parameters.AddWithValue("sessions", NpgsqlDbType.Array | NpgsqlDbType.Varchar,
                required.Select(member => member.Session.SessionId)
                    .Append(manifest.Root.SessionId).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new CheckpointRecoveryReadyRuntimeRow(
                    reader.GetString(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetBoolean(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8).Trim(),
                    reader.IsDBNull(9) ? null : reader.GetInt16(9),
                    reader.IsDBNull(10) ? null : new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero),
                    reader.GetGuid(11),
                    reader.GetGuid(12),
                    reader.GetGuid(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetGuid(15),
                    reader.GetInt64(16),
                    reader.GetString(17),
                    reader.GetString(18).Trim());
                if (!bySession.TryAdd(row.SessionId, row))
                    return false;
            }
        }

        if (!bySession.TryGetValue(manifest.Root.SessionId, out var root) ||
            root.IntentId != rootIntent.IntentId ||
            root.State != rootIntent.State || root.Step != rootIntent.CurrentStep ||
            root.Fence != rootIntent.ExecutionFence || root.Cleanup != rootIntent.CleanupObligation ||
            root.RowVersion != rootIntent.RowVersion ||
            root.BootstrapId != rootIntent.BootstrapOperationId ||
            root.ConsumeId != rootIntent.ConsumeOperationId ||
            root.ExchangeId != rootIntent.ExchangeOperationId ||
            root.EnvironmentId != rootIntent.EnvironmentId ||
            root.RuntimeInstanceId != rootIntent.RuntimeInstanceId ||
            root.TenantId != rootIntent.TenantId ||
            !string.Equals(root.SelectionHash, rootIntent.AcceptedSelectionHash, StringComparison.OrdinalIgnoreCase) ||
            !SameJson(root.Links, rootIntent.ReceiptLinks) ||
            !IsCheckpointRecoveryRuntimeReady(
                manifest, root, manifest.RootStartup.IntentId, manifest.RootStartup.RuntimeInstanceId,
                manifest.RootStartup.RegistrationRevision, manifest.Root.SessionId,
                CoordinationEndpoints.CoordinatorRuntimeAgentId, workflowStepId: null,
                modelSelectionReference: null, logicalTurnOrdinal: 0,
                manifest.RootStartup.Placement,
                RuntimeRunStartKeys.ReplacementOperationId(manifest.OperationId, "checkpoint-bootstrap"),
                RuntimeRunStartKeys.ReplacementOperationId(manifest.OperationId, "checkpoint-consume"),
                RuntimeRunStartKeys.ReplacementOperationId(manifest.OperationId, "checkpoint-exchange"),
                member: null))
            return false;

        foreach (var member in required)
        {
            if (!bySession.TryGetValue(member.Session.SessionId, out var intent) ||
                member.Startup.Placement is not { } previousPlacement ||
                !IsCheckpointRecoveryRuntimeReady(
                    manifest, intent, member.Startup.IntentId, member.Startup.RegistrationId,
                    member.Startup.RegistrationRevision, member.Session.SessionId, member.AgentId,
                    member.WorkflowStepId, member.ModelSelectionReference, member.Session.LogicalTurnOrdinal,
                    previousPlacement,
                    RuntimeRunStartKeys.ReplacementOperationId(
                        manifest.OperationId, member.Session.SessionId, "checkpoint-bootstrap"),
                    RuntimeRunStartKeys.ReplacementOperationId(
                        manifest.OperationId, member.Session.SessionId, "checkpoint-consume"),
                    RuntimeRunStartKeys.ReplacementOperationId(
                        manifest.OperationId, member.Session.SessionId, "checkpoint-exchange"),
                    member))
                return false;
        }
        return true;
    }

    private bool IsCheckpointRecoveryRuntimeReady(
        ChildCheckpointRecoveryManifest manifest,
        CheckpointRecoveryReadyRuntimeRow intent,
        Guid expectedIntentId,
        Guid? expectedRuntimeInstanceId,
        long? sourceRegistrationRevision,
        string sessionId,
        string agentId,
        string? workflowStepId,
        string? modelSelectionReference,
        long logicalTurnOrdinal,
        RuntimeRunStartPlacementReceipt previousPlacement,
        Guid bootstrapOperationId,
        Guid consumeOperationId,
        Guid exchangeOperationId,
        ChildRecoveryMember? member)
    {
        if (intent.IntentId != expectedIntentId ||
            intent.State != "ready" || intent.Step != "ready" ||
            intent.Fence != manifest.HoldFence || intent.Cleanup ||
            intent.BootstrapId != bootstrapOperationId ||
            intent.ConsumeId != consumeOperationId ||
            intent.ExchangeId != exchangeOperationId ||
            intent.TenantId != manifest.TenantId ||
            !string.Equals(intent.SelectionHash, manifest.SelectionHash, StringComparison.OrdinalIgnoreCase) ||
            intent.RegistrationRevision is not { } registrationRevision ||
            intent.RegistrationState != (short)RuntimeRegistrationState.Active ||
            intent.RegistrationBindingHash is null ||
            intent.RegistrationExpiresAt is not { } registrationExpiresAt ||
            registrationExpiresAt <= _timeProvider.GetUtcNow())
            return false;
        try
        {
            if (expectedRuntimeInstanceId is not { } previousRuntimeInstanceId ||
                sourceRegistrationRevision is not { } previousRegistrationRevision ||
                previousRegistrationRevision <= 0 || previousRegistrationRevision == long.MaxValue)
                return false;
            var replacementRuntimeInstanceId = RuntimeRegistrationStore.CreateReplacementId(
                previousRuntimeInstanceId, manifest.OperationId);
            using var links = JsonDocument.Parse(intent.Links);
            var root = links.RootElement;
            if (member is not null &&
                root.GetProperty("childStart").Deserialize<RuntimeChildStartBinding>(JsonOptions) !=
                    member.Startup.Binding)
                return false;
            if (root.GetProperty("registration").Deserialize<RuntimeRegistration>(JsonOptions) is not
                    { State: RuntimeRegistrationState.Active } registration ||
                registration.RuntimeInstanceId != replacementRuntimeInstanceId ||
                registration.Revision != 1 ||
                registration.Revision != registrationRevision ||
                registration.ExpiresAt != registrationExpiresAt ||
                registration.ExpiresAt <= _timeProvider.GetUtcNow() ||
                intent.RuntimeInstanceId != registration.RuntimeInstanceId ||
                intent.EnvironmentId is null ||
                registration.Binding.TenantId != manifest.TenantId ||
                registration.Binding.ActorIssuer != manifest.ActorIssuer ||
                registration.Binding.ActorId != manifest.ActorId ||
                registration.Binding.ProjectId != manifest.Root.ProjectId ||
                registration.Binding.RunId != manifest.Root.RunId ||
                registration.Binding.SessionId != sessionId ||
                registration.Binding.AgentId != agentId ||
                registration.Binding.WorkflowStepId != workflowStepId ||
                modelSelectionReference is not null &&
                    registration.Binding.ModelSelectionReference != modelSelectionReference ||
                !string.Equals(
                    registration.Binding.AcceptedSelectionHash, manifest.SelectionHash,
                    StringComparison.OrdinalIgnoreCase) ||
                registration.Binding.ExecutionFence != manifest.HoldFence ||
                registration.Binding.TurnId != (logicalTurnOrdinal == 0
                    ? CreateCoordinatorRootRuntimeTurnId(manifest.Root, manifest.HoldFence)
                    : CreateRuntimeTurnId(new SessionIdentity(
                            manifest.Root.ProjectId, manifest.Root.RunId, sessionId),
                        manifest.HoldFence, logicalTurnOrdinal)) ||
                intent.RegistrationBindingHash != RuntimeContractValidation.Hash(
                    JsonSerializer.SerializeToUtf8Bytes(
                        registration.Binding, RuntimeRegistrationJsonOptions)))
                return false;
            RuntimeContractValidation.Validate(registration);
            var placement = root.GetProperty("placement")
                .Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions);
            var activation = root.GetProperty("activation")
                .Deserialize<RuntimeHostReadinessReceipt>(JsonOptions);
            var readiness = root.GetProperty("readiness")
                .Deserialize<EnvironmentRuntimeReadinessContext>(JsonOptions);
            if (placement is null || activation is null || readiness?.Placement is null ||
                readiness.Observation is null || registration.Binding.EnvironmentId != previousPlacement.EnvironmentId ||
                intent.EnvironmentId != placement.EnvironmentId ||
                placement.EnvironmentId != previousPlacement.EnvironmentId ||
                placement.SandboxResource is null ||
                registration.Binding.EnvironmentId != placement.EnvironmentId ||
                registration.Binding.PlacementUid != placement.SandboxResource.ResourceId ||
                registration.Binding.PlacementGeneration != placement.SandboxResource.Generation)
                return false;
            previousPlacement.Validate();
            placement.Validate();
            var owner = RuntimeRunStartContinuation.CreateOwnerContext(registration.Binding);
            RuntimeRunStartContinuation.RequireReplacementRegistration(owner, placement, registration);
            RuntimeRunStartContinuation.RequireReplacementPlacement(
                previousPlacement, placement, bootstrapOperationId, owner);
            CoordinationEndpoints.ValidateHostReadiness(activation, registration, _timeProvider);
            var recovery = activation.Recovery?.ValidateFor(registration);
            if (recovery?.RecoveryMode is not (RuntimeHostRecoveryMode.NativeCache or
                RuntimeHostRecoveryMode.JournalRebuild))
                return false;
            RuntimeRunStartContinuation.RequirePlacement(placement, registration, readiness.Placement);
            RuntimeRunStartContinuation.RequireCurrentReadiness(
                _timeProvider, placement, registration, activation, readiness);
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or KeyNotFoundException or
                RuntimeAuthorizationException or CoordinationException or ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    private static bool SameJson(string left, string right)
    {
        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return ChildCheckpointRecoveryManifestCodec.Hash(leftDocument.RootElement) ==
                ChildCheckpointRecoveryManifestCodec.Hash(rightDocument.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal async Task<RuntimeRunStartIntent> PersistCheckpointRecoveryCleanupEvidenceAsync(
        RuntimeRunStartIntent intent,
        string receiptLinks,
        long executionFence,
        Guid bootstrapOperationId,
        string? environmentId,
        Guid? runtimeInstanceId,
        CancellationToken cancellationToken,
        string? currentStep = null,
        bool ready = false,
        Guid? consumeOperationId = null,
        Guid? exchangeOperationId = null)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_schema}.runtime_run_start_intents
            SET execution_fence = @current_fence,
                bootstrap_operation_id = @bootstrap,
                consume_operation_id = COALESCE(@consume, consume_operation_id),
                exchange_operation_id = COALESCE(@exchange, exchange_operation_id),
                state = CASE WHEN @ready THEN 'ready' ELSE state END,
                current_step = COALESCE(@step, current_step),
                failure_code = CASE WHEN @ready THEN NULL ELSE failure_code END,
                environment_id = @environment,
                runtime_instance_id = @runtime,
                receipt_links = @receipts,
                row_version = row_version + 1,
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
                AND root_session_id = @root AND accepted_selection_hash = @selection
                AND execution_fence = @fence AND row_version = @version
                AND state = 'ready' AND NOT cleanup_obligation
            RETURNING row_version
            """, connection);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, intent.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, intent.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, intent.RunId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, intent.RootSessionId);
        command.Parameters.AddWithValue("selection", NpgsqlDbType.Char, intent.AcceptedSelectionHash);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, intent.ExecutionFence);
        command.Parameters.AddWithValue("current_fence", NpgsqlDbType.Bigint, executionFence);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, intent.RowVersion);
        command.Parameters.AddWithValue("bootstrap", NpgsqlDbType.Uuid, bootstrapOperationId);
        command.Parameters.AddWithValue(
            "consume", NpgsqlDbType.Uuid, (object?)consumeOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "exchange", NpgsqlDbType.Uuid, (object?)exchangeOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("ready", NpgsqlDbType.Boolean, ready);
        command.Parameters.AddWithValue("step", NpgsqlDbType.Varchar, (object?)currentStep ?? DBNull.Value);
        command.Parameters.AddWithValue("environment", NpgsqlDbType.Varchar, (object?)environmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, (object?)runtimeInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("receipts", NpgsqlDbType.Jsonb, receiptLinks);
        var rowVersion = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
        if (rowVersion is null)
            throw new CoordinationException("runtime_start_intent_stale", StatusCodes.Status409Conflict);
        return intent with
        {
            ExecutionFence = executionFence,
            BootstrapOperationId = bootstrapOperationId,
            ConsumeOperationId = consumeOperationId ?? intent.ConsumeOperationId,
            ExchangeOperationId = exchangeOperationId ?? intent.ExchangeOperationId,
            State = ready ? "ready" : intent.State,
            CurrentStep = currentStep ?? intent.CurrentStep,
            FailureCode = ready ? null : intent.FailureCode,
            EnvironmentId = environmentId,
            RuntimeInstanceId = runtimeInstanceId,
            ReceiptLinks = receiptLinks,
            RowVersion = rowVersion.Value
        };
    }

    /// <summary>
    /// Hashes the operation kind, run identity, and request fields used for run-transition idempotency.
    /// </summary>
    private static string HashRunExecutionOperation(string operationKind, SessionIdentity identity, object request)
    {
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new { operationKind, identity, request }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(requestBytes));
    }

    /// <summary>
    /// Validates the versions, cause, reference, and idempotency key for a reported run failure.
    /// </summary>
    private static void ValidateRunFailureRequest(ReportRunFailureRequest request)
    {
        if (request.ExecutionFence < 1 || request.ExpectedRunStateVersion < 1 ||
            request.ExpectedSessionStateVersion < 1 || !Enum.IsDefined(request.State))
            throw new CoordinationException("execution_failure_invalid", StatusCodes.Status400BadRequest);
        ValidateExecutionOperationIdentity(request.IdempotencyKey, request.CauseCode, request.Reference);
    }

    /// <summary>
    /// Validates the versions, cause, reference, and idempotency key for run recovery.
    /// </summary>
    private static void ValidateRunRecoveryRequest(RecoverRunExecutionRequest request)
    {
        if (request.ExecutionFence < 1 || request.ExpectedRunStateVersion < 1)
            throw new CoordinationException("execution_recovery_invalid", StatusCodes.Status400BadRequest);
        ValidateExecutionOperationIdentity(request.IdempotencyKey, request.CauseCode, request.Reference);
        if (request.CheckpointRecovery is { } checkpoint &&
            (string.IsNullOrWhiteSpace(checkpoint.SourceCheckpointId) ||
             checkpoint.SourceCheckpointId.Length > 128 ||
             checkpoint.SourceCheckpointRevision < 1 ||
             checkpoint.ExpectedDecisionStateVersion < 1 ||
             checkpoint.RuntimeInstanceId == Guid.Empty ||
             checkpoint.RegistrationRevision < 1 ||
             checkpoint.RegistrationRevision == long.MaxValue ||
             string.IsNullOrWhiteSpace(checkpoint.AcceptedSelectionHash) ||
             checkpoint.AcceptedSelectionHash.Length != 64 ||
             !checkpoint.AcceptedSelectionHash.All(Uri.IsHexDigit)))
            throw new CoordinationException(
                "checkpoint_recovery_invalid", StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Checks that a run-transition request uses a valid operation key and cause reference.
    /// </summary>
    private static void ValidateExecutionOperationIdentity(
        string idempotencyKey,
        string causeCode,
        string reference)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 ||
            string.IsNullOrWhiteSpace(causeCode) || causeCode.Length > 128 ||
            string.IsNullOrWhiteSpace(reference) || reference.Length > 256)
            throw new CoordinationException("execution_operation_invalid", StatusCodes.Status400BadRequest);
        CoordinationIdentity.ValidateIdentity(idempotencyKey, "idempotency_key");
        CoordinationIdentity.ValidateIdentity(causeCode, "cause_code");
        CoordinationIdentity.ValidateIdentity(reference, "reference");
    }

    /// <summary>
    /// Counts sessions still bound to the specified execution fence before a recovery transition.
    /// </summary>
    private async Task<int> ReadRunSessionCountAtFenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        long executionFence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT count(*), count(*) FILTER (WHERE execution_fence <> @fence)
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt64(0) < 1 || reader.GetInt64(1) != 0)
            throw new CoordinationException("execution_fence_inconsistent", StatusCodes.Status503ServiceUnavailable);
        return checked((int)reader.GetInt64(0));
    }

    /// <summary>
    /// Reads the accepted run binding and optionally locks it for a coordinated mutation.
    /// </summary>
    /// <exception cref="CoordinationException">The accepted run does not exist.</exception>
    private async Task<AcceptedRunRow> ReadAcceptedRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        bool forUpdate,
        CancellationToken cancellationToken,
        bool forShare = false)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT accepted_selection_hash, accepted_by_issuer, accepted_by_subject,
                execution_fence, logical_turn_ordinal, execution_state, state_version, tenant_id,
                execution_cause_code, execution_reference
            FROM {_runs}
            WHERE project_id = @project AND run_id = @run
            {(forUpdate ? "FOR UPDATE" : forShare ? "FOR SHARE" : string.Empty)}
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new CoordinationException("accepted_run_not_found", StatusCodes.Status404NotFound);
        return new AcceptedRunRow(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    /// <summary>
    /// Reads one session row and optionally locks it, returning not found when it is absent.
    /// </summary>
    private async Task<SessionRow> ReadSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var session = await FindSessionAsync(
            connection, transaction, identity.ProjectId, identity.RunId,
            identity.SessionId, forUpdate, cancellationToken).ConfigureAwait(false);
        return session ?? throw new CoordinationException("session_not_found", StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Maps the selected database columns into the internal session state record.
    /// </summary>
    private static SessionRow ReadSessionRow(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.GetInt64(9),
            reader.GetString(10),
            reader.GetInt64(11),
            reader.GetBoolean(12),
            reader.IsDBNull(13) ? null : reader.GetInt64(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.GetFieldValue<DateTimeOffset>(15),
            reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
            reader.IsDBNull(17) ? null : reader.GetString(17));

    /// <summary>
    /// Looks up a session by project, run, and identifier, with an optional row lock.
    /// </summary>
    private async Task<SessionRow?> FindSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        string sessionId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT session_id, parent_session_id, root_session_id, node_kind, detached,
                writer_issuer, writer_subject, execution_fence, lifecycle_state, logical_turn_ordinal,
                turn_state, state_version, pending_wake, turn_boundary_request_version,
                turn_boundary_result, created_at, archived_at, work_plan_item_id
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.GetInt64(9),
                reader.GetString(10),
                reader.GetInt64(11),
                reader.GetBoolean(12),
                reader.IsDBNull(13) ? null : reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.GetFieldValue<DateTimeOffset>(15),
                reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
                reader.IsDBNull(17) ? null : reader.GetString(17))
            : null;
    }

    /// <summary>
    /// Reads the unique root session row for a run, with an optional row lock.
    /// </summary>
    private async Task<SessionRow?> ReadRootSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT session_id, parent_session_id, root_session_id, node_kind, detached,
                writer_issuer, writer_subject, execution_fence, lifecycle_state, logical_turn_ordinal,
                turn_state, state_version, pending_wake, turn_boundary_request_version,
                turn_boundary_result, created_at, archived_at, work_plan_item_id
            FROM {_sessions}
            WHERE project_id = @project AND run_id = @run AND parent_session_id IS NULL
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionRow(
                reader.GetString(0),
                null,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.GetInt64(9),
                reader.GetString(10),
                reader.GetInt64(11),
                reader.GetBoolean(12),
                reader.IsDBNull(13) ? null : reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.GetFieldValue<DateTimeOffset>(15),
                reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
                reader.IsDBNull(17) ? null : reader.GetString(17))
            : null;
    }

    /// <summary>
    /// Finds the most recent pending request between a sender and recipient.
    /// </summary>
    private async Task<string?> FindRequestIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        string senderSessionId,
        string recipientSessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT request_id FROM {_requests}
            WHERE project_id = @project AND run_id = @run
                AND sender_session_id = @sender AND recipient_session_id = @recipient
                AND gate_state = 'pending'
            ORDER BY created_at DESC LIMIT 1
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, senderSessionId);
        command.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipientSessionId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// Checks whether a matching request exists and has not been cancelled.
    /// </summary>
    private async Task<bool> HasActiveRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        string senderSessionId,
        string recipientSessionId,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_requests}
                WHERE project_id = @project AND run_id = @run AND request_id = @request
                    AND sender_session_id = @sender AND recipient_session_id = @recipient
                    AND gate_state <> 'cancelled'
            )
            """, connection, transaction);
        AddRunScope(command, projectId, runId);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, requestId);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, senderSessionId);
        command.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipientSessionId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    /// <summary>
    /// Reads the thread identifier for an eligible reply target and locks the matched message row.
    /// </summary>
    private async Task<Guid?> ReadReverseMessageThreadIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity sender,
        SessionIdentity recipient,
        Guid replyTo,
        string? requestId,
        string? replyCorrelationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT thread_id
            FROM {_messages}
            WHERE project_id = @project AND run_id = @run AND message_id = @replyTo
                AND sender_session_id = @recipient AND recipient_session_id = @sender
                AND (@request IS NULL OR request_id = @request)
                AND (@correlation IS NULL OR request_id = @correlation
                    OR reply_correlation_id = @correlation)
                AND status <> 'cancelled'
            FOR UPDATE
            """, connection, transaction);
        AddRunScope(command, sender.ProjectId, sender.RunId);
        command.Parameters.AddWithValue("replyTo", NpgsqlDbType.Uuid, replyTo);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
        command.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipient.SessionId);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, (object?)requestId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "correlation", NpgsqlDbType.Varchar, (object?)replyCorrelationId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? reader.GetGuid(0) : null;
    }

    /// <summary>
    /// Checks whether an eligible reverse-direction message exists for a reply or correlation.
    /// </summary>
    private async Task<bool> HasReverseMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity sender,
        SessionIdentity recipient,
        Guid replyTo,
        string? requestId,
        string? replyCorrelationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {_messages}
                WHERE project_id = @project AND run_id = @run AND message_id = @replyTo
                    AND sender_session_id = @recipient AND recipient_session_id = @sender
                    AND (@request IS NULL OR request_id = @request)
                    AND (@correlation IS NULL OR request_id = @correlation
                        OR reply_correlation_id = @correlation)
                    AND status <> 'cancelled'
            )
            """, connection, transaction);
        AddRunScope(command, sender.ProjectId, sender.RunId);
        command.Parameters.AddWithValue("replyTo", NpgsqlDbType.Uuid, replyTo);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
        command.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipient.SessionId);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, (object?)requestId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "correlation", NpgsqlDbType.Varchar, (object?)replyCorrelationId ?? DBNull.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    /// <summary>
    /// Reads the Events message identifier for an acknowledged reply matching the requested correlation.
    /// </summary>
    private async Task<Guid?> ReadAcknowledgedReplyMessageIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity sender,
        SessionIdentity recipient,
        Guid replyTo,
        string? requestId,
        string? replyCorrelationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT events_message_id
            FROM {_messages}
            WHERE project_id = @project AND run_id = @run AND message_id = @replyTo
                AND sender_session_id = @recipient AND recipient_session_id = @sender
                AND status = 'acknowledged'
                AND (@request IS NULL OR request_id = @request)
                AND (@correlation IS NULL OR request_id = @correlation
                    OR reply_correlation_id = @correlation)
            """, connection, transaction);
        AddRunScope(command, sender.ProjectId, sender.RunId);
        command.Parameters.AddWithValue("replyTo", NpgsqlDbType.Uuid, replyTo);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
        command.Parameters.AddWithValue("recipient", NpgsqlDbType.Varchar, recipient.SessionId);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Varchar, (object?)requestId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "correlation", NpgsqlDbType.Varchar, (object?)replyCorrelationId ?? DBNull.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>
    /// Reads a message previously submitted with the same sender, actor, and idempotency key.
    /// </summary>
    private async Task<ExistingMessage?> ReadExistingMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity sender,
        CoordinationActor actor,
        string idempotencyKey,
        JsonElement canonicalRequest,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT message_id, recipient_session_id, status, request_id,
                canonical_request = @canonical
            FROM {_messages}
            WHERE project_id = @project AND run_id = @run AND sender_session_id = @sender
                AND writer_issuer = @issuer AND writer_subject = @subject AND idempotency_key = @key
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, sender.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, sender.RunId);
        command.Parameters.AddWithValue("sender", NpgsqlDbType.Varchar, sender.SessionId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, idempotencyKey);
        command.Parameters.AddWithValue("canonical", NpgsqlDbType.Jsonb, canonicalRequest.GetRawText());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ExistingMessage(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4))
            : null;
    }

    /// <summary>
    /// Reads the owner-side message fields needed to verify an admission receipt or outbound delivery.
    /// </summary>
    private async Task<StoredMessage?> ReadMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid messageId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT project_id, run_id, sender_session_id, recipient_session_id, thread_id,
                writer_issuer, writer_subject, sender_fence, recipient_fence,
                purpose, kind, delivery_mode, request_id, reply_to_id, status
            FROM {_messages}
            WHERE message_id = @message
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """, connection, transaction);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredMessage(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetGuid(4), reader.GetString(5), reader.GetString(6),
                reader.GetInt64(7), reader.GetInt64(8),
                Enum.Parse<AddressedMessagePurpose>(reader.GetString(9)),
                Enum.Parse<AddressedMessageKind>(reader.GetString(10)),
                Enum.Parse<AddressedMessageDeliveryMode>(reader.GetString(11)),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetGuid(13),
                reader.GetString(14))
            : null;
    }

    /// <summary>
    /// Verifies that an Events admission receipt matches the owner message and both session fences.
    /// </summary>
    private static bool ReceiptMatches(StoredMessage stored, MessageAdmissionReceipt receipt) =>
        receipt.OwnerMessageId != Guid.Empty &&
        stored.ProjectId == receipt.Sender.ProjectId &&
        stored.RunId == receipt.Sender.RunId &&
        stored.ProjectId == receipt.Recipient.ProjectId &&
        stored.RunId == receipt.Recipient.RunId &&
        stored.SenderSessionId == receipt.Sender.SessionId &&
        stored.RecipientSessionId == receipt.Recipient.SessionId &&
        stored.ThreadId == receipt.ThreadId &&
        stored.SenderFence == receipt.SenderFence &&
        stored.RecipientFence == receipt.RecipientFence &&
        stored.Purpose == receipt.Purpose &&
        stored.RequestId == receipt.RequestId &&
        receipt.Status == AddressedMessageStatus.Accepted &&
        receipt.MessageId != Guid.Empty &&
        receipt.ThreadSequence > 0 &&
        receipt.Claim is null;

    private static AddressedMessageDraft ToAddressedMessageDraft(OwnerOutboundMessage outbound) =>
        new(
            outbound.Sender,
            outbound.Recipient,
            outbound.IdempotencyKey,
            outbound.DeliveryMode,
            outbound.Purpose,
            outbound.Kind,
            outbound.Payload,
            outbound.SenderFence,
            outbound.RecipientFence,
            outbound.ThreadId,
            outbound.ReplyToId,
            outbound.RequestId,
            outbound.ReplyCorrelationId,
            outbound.UserQuote,
            outbound.CoordinatorInstructions);

    /// <summary>
    /// Compares all immutable fields before accepting an outbound message supplied by a delivery worker.
    /// </summary>
    private static bool SameOutboundMessage(
        OwnerOutboundMessage persisted,
        OwnerOutboundMessage supplied) =>
        persisted.OwnerMessageId == supplied.OwnerMessageId &&
        persisted.Sender == supplied.Sender &&
        persisted.Recipient == supplied.Recipient &&
        persisted.IdempotencyKey == supplied.IdempotencyKey &&
        persisted.DeliveryMode == supplied.DeliveryMode &&
        persisted.Purpose == supplied.Purpose &&
        persisted.Kind == supplied.Kind &&
        JsonElement.DeepEquals(persisted.Payload, supplied.Payload) &&
        persisted.SenderFence == supplied.SenderFence &&
        persisted.RecipientFence == supplied.RecipientFence &&
        persisted.ThreadId == supplied.ThreadId &&
        persisted.ReplyToId == supplied.ReplyToId &&
        persisted.RequestId == supplied.RequestId &&
        persisted.ReplyCorrelationId == supplied.ReplyCorrelationId &&
        persisted.UserQuote == supplied.UserQuote &&
        persisted.CoordinatorInstructions == supplied.CoordinatorInstructions;

    /// <summary>
    /// Projects a stored session row into the public tree-node contract.
    /// </summary>
    private static SessionTreeNode ToSessionTreeNode(SessionRow session, SessionIdentity identity) =>
        new(
            identity,
            session.ParentSessionId,
            session.RootSessionId,
            ParseSessionKind(session.NodeKind),
            session.Detached,
            ParseLifecycle(session.LifecycleState),
            session.ExecutionFence,
            session.LogicalTurnOrdinal,
            session.StateVersion,
            session.CreatedAt,
            session.ArchivedAt);

    /// <summary>
    /// Maps a stored session kind and fails closed for values outside the contract.
    /// </summary>
    private static CoordinationSessionKind ParseSessionKind(string value) => value switch
    {
        "coordinator" => CoordinationSessionKind.Coordinator,
        "child_work" => CoordinationSessionKind.ChildWork,
        "scribe" => CoordinationSessionKind.Scribe,
        "operator_chat" => CoordinationSessionKind.OperatorChat,
        "child_run" => CoordinationSessionKind.ChildRun,
        _ => throw new CoordinationException(
            "session_tree_state_unavailable", StatusCodes.Status503ServiceUnavailable)
    };

    /// <summary>
    /// Maps a stored lifecycle state and rejects unknown values as unavailable.
    /// </summary>
    private static CoordinationLifecycleState ParseLifecycle(string value) => value switch
    {
        "active" => CoordinationLifecycleState.Active,
        "cancelled" => CoordinationLifecycleState.Cancelled,
        "completed" => CoordinationLifecycleState.Completed,
        "archived" => CoordinationLifecycleState.Archived,
        _ => throw new CoordinationException(
            "session_tree_state_unavailable", StatusCodes.Status503ServiceUnavailable)
    };

    /// <summary>
    /// Maps a supported child kind to its persisted representation.
    /// </summary>
    private static string ToDatabaseSessionKind(CoordinationSessionKind kind) => kind switch
    {
        CoordinationSessionKind.ChildWork => "child_work",
        CoordinationSessionKind.Scribe => "scribe",
        CoordinationSessionKind.OperatorChat => "operator_chat",
        CoordinationSessionKind.ChildRun => "child_run",
        _ => throw new CoordinationException(
            "session_node_kind_invalid", StatusCodes.Status400BadRequest)
    };

    /// <summary>
    /// Maps a stored idle-notification mode and rejects unknown values as unavailable.
    /// </summary>
    private static IdleNotificationMode ParseIdleMode(string value) => value switch
    {
        "once" => IdleNotificationMode.Once,
        "always" => IdleNotificationMode.Always,
        _ => throw new CoordinationException(
            "idle_subscription_unavailable", StatusCodes.Status503ServiceUnavailable)
    };

    /// <summary>
    /// Maps a supported idle-notification mode to its persisted representation.
    /// </summary>
    private static string ToDatabaseIdleMode(IdleNotificationMode mode) => mode switch
    {
        IdleNotificationMode.Once => "once",
        IdleNotificationMode.Always => "always",
        _ => throw new CoordinationException(
            "idle_subscription_invalid", StatusCodes.Status400BadRequest)
    };

    /// <summary>
    /// Requires the actor to match the issuer and subject that own the session row.
    /// </summary>
    /// <exception cref="CoordinationException">The actor does not own the session.</exception>
    private static void RequireWriter(SessionRow session, CoordinationActor actor)
    {
        if (session.WriterIssuer != actor.Issuer || session.WriterSubject != actor.Subject)
            throw new CoordinationException("session_writer_mismatch", StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Requires the actor to match the issuer and subject that accepted the run.
    /// </summary>
    private static void RequireRunOwner(AcceptedRunRow run, CoordinationActor actor)
    {
        if (run.AcceptedIssuer != actor.Issuer || run.AcceptedSubject != actor.Subject)
            throw new CoordinationException("run_owner_mismatch", StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Rejects stale-fence, failed-run, or non-active session state before active work proceeds.
    /// </summary>
    /// <exception cref="CoordinationException">The session is stale, inactive, or requires run recovery.</exception>
    private static void RequireCurrentActiveSession(SessionRow session, AcceptedRunRow run)
    {
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        if (run.ExecutionState == "cancelled")
            throw new CoordinationException("run_terminal", StatusCodes.Status409Conflict);
        if (run.ExecutionState is "failed" or "indeterminate")
            throw new CoordinationException("execution_recovery_required", StatusCodes.Status409Conflict);
        if (session.LifecycleState != "active")
            throw new CoordinationException("session_unavailable", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Requires positive accepted revisions, a context revision, and an object-valued selection snapshot.
    /// </summary>
    private static void ValidateSelection(EffectiveRunSelection selection)
    {
        if (selection.ProjectRevision < 1 || selection.ProjectConfigurationRevision < 1 ||
            selection.PlatformRuntimeRevision < 1 || string.IsNullOrWhiteSpace(selection.ContextRevision) ||
            selection.Snapshot.ValueKind != JsonValueKind.Object)
            throw new CoordinationException("accepted_run_selection_invalid", StatusCodes.Status400BadRequest);
        CoordinationIdentity.ValidateIdentity(selection.ProjectId, "project_id");
        CoordinationIdentity.ValidateIdentity(selection.RunId, "run_id");
    }

    /// <summary>
    /// Adds the project and run parameters shared by owner-scoped SQL commands.
    /// </summary>
    private static void AddRunScope(NpgsqlCommand command, string projectId, string runId)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
    }

    /// <summary>
    /// Derives a stable opaque runtime turn identifier from the session, execution fence, and turn ordinal.
    /// </summary>
    internal static string CreateRuntimeTurnId(
        SessionIdentity identity,
        long executionFence,
        long logicalTurnOrdinal)
    {
        if (executionFence < 1 || logicalTurnOrdinal < 1)
            throw new InvalidOperationException("A runtime turn ID requires a current fence and turn ordinal.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Contract = "agentweaver.runtime-turn.v1",
            identity.ProjectId,
            identity.RunId,
            identity.SessionId,
            ExecutionFence = executionFence,
            LogicalTurnOrdinal = logicalTurnOrdinal
        });
        return $"turn-{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    internal static string CreateCoordinatorRootRuntimeTurnId(
        SessionIdentity identity,
        long executionFence)
    {
        if (executionFence < 1)
            throw new InvalidOperationException("A coordinator runtime turn ID requires a current fence.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Contract = "agentweaver.coordinator-runtime-turn.v1",
            identity.ProjectId,
            identity.RunId,
            identity.SessionId,
            ExecutionFence = executionFence
        });
        return $"turn-{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    /// <summary>
    /// Rejects invalid and system-reserved PostgreSQL schema names.
    /// </summary>
    private static void ValidateSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || schema.Length > 63 ||
            schema[0] is < 'a' or > 'z' ||
            schema.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_') ||
            schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
    }

    /// <summary>
    /// Holds the accepted run binding and its current execution state as read from PostgreSQL.
    /// </summary>
    private sealed record AcceptedRunRow(
        string SelectionHash,
        string AcceptedIssuer,
        string AcceptedSubject,
        long Fence,
        long LogicalTurnOrdinal,
        string ExecutionState,
        long StateVersion,
        string TenantId,
        string? ExecutionCauseCode,
        string? ExecutionReference);

    /// <summary>
    /// Holds persisted session-tree, turn, fencing, and work-plan association state.
    /// </summary>
    private sealed record SessionRow(
        string SessionId,
        string? ParentSessionId,
        string RootSessionId,
        string NodeKind,
        bool Detached,
        string WriterIssuer,
        string WriterSubject,
        long ExecutionFence,
        string LifecycleState,
        long LogicalTurnOrdinal,
        string TurnState,
        long StateVersion,
        bool PendingWake,
        long? TurnBoundaryRequestVersion,
        string? TurnBoundaryResult,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ArchivedAt,
        string? WorkPlanItemId);

    /// <summary>
    /// Returns the registered child together with a spawn result when the command requested one.
    /// </summary>
    private sealed record SessionRegistrationResult(
        RegisteredChild Registered,
        SpawnedSession? Spawned);

    /// <summary>
    /// Holds the persisted subscriber and notification mode for an idle subscription.
    /// </summary>
    private sealed record IdleSubscriptionRow(
        Guid SubscriptionId,
        string SubscriberSessionId,
        IdleNotificationMode Mode);

    /// <summary>
    /// Holds the stored result needed to determine whether a message retry is an exact duplicate.
    /// </summary>
    private sealed record ExistingMessage(
        Guid MessageId,
        string RecipientSessionId,
        string Status,
        string? RequestId,
        bool CanonicalRequestMatches);

    /// <summary>
    /// Holds immutable message identity, routing, writer, fence, and delivery fields from the owner store.
    /// </summary>
    private sealed record StoredMessage(
        string ProjectId,
        string RunId,
        string SenderSessionId,
        string RecipientSessionId,
        Guid ThreadId,
        string WriterIssuer,
        string WriterSubject,
        long SenderFence,
        long RecipientFence,
        AddressedMessagePurpose Purpose,
        AddressedMessageKind Kind,
        AddressedMessageDeliveryMode DeliveryMode,
        string? RequestId,
        Guid? ReplyToId,
        string Status);
}
