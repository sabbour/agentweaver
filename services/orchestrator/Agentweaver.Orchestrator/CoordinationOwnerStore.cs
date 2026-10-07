using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Persistence.Postgres;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed partial class CoordinationOwnerStore
{
    private const string IngressConsumer = "orchestrator.addressed-message-ingress";
    private const int EventVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
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
    private readonly TimeProvider _timeProvider;
    private readonly CoordinatorDecisionOwnerStore? _decisionStore;

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
        _timeProvider = timeProvider ?? TimeProvider.System;
        _decisionStore = decisionStore;
    }

    public async Task<AcceptedRoot> AcceptRootAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        string sessionId,
        CancellationToken cancellationToken)
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

        var snapshot = effectiveSelection.Snapshot.GetRawText();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
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
            await insert.ExecuteNonQueryAsync(cancellationToken);
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
        if (root is null)
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
        if (root is null || root.SessionId != sessionId ||
            root.WriterIssuer != actor.Issuer || root.WriterSubject != actor.Subject)
            throw new CoordinationException("root_session_conflict", StatusCodes.Status409Conflict);

        await transaction.CommitAsync(cancellationToken);
        return new AcceptedRoot(
            effectiveSelection.ProjectId,
            effectiveSelection.RunId,
            root.SessionId,
            acceptedRun.Fence,
            acceptedRun.StateVersion,
            acceptedRun.LogicalTurnOrdinal,
            acceptedRun.ExecutionState);
    }

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

    public async Task<SpawnedSession> SpawnSessionAsync(
        CoordinationActor actor,
        SessionIdentity parent,
        SpawnSessionRequest request,
        int maxChildren,
        int maxConcurrentChildren,
        CancellationToken cancellationToken,
        ConfirmedWorkPlanItemAssociation? workPlanItemAssociation = null,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null)
    {
        ValidateSpawnRequest(request);
        var result = await RegisterChildCoreAsync(
            actor, parent, request.SessionId, request.Kind, request,
            workPlanItemAssociation, maxChildren, maxConcurrentChildren, cancellationToken,
            revalidateCurrentAuthority).ConfigureAwait(false);
        return result.Spawned
            ?? throw new CoordinationException("session_spawn_unavailable", StatusCodes.Status503ServiceUnavailable);
    }

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

    public async Task<CoordinationSessionForkResult> PrepareSessionForkAsync(
        CoordinationActor actor,
        SessionIdentity source,
        CoordinationSessionForkRequest request,
        string acceptedSelectionHash,
        int maxChildren,
        int maxConcurrentChildren,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null)
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
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null)
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

        if (revalidateCurrentAuthority is not null)
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return new SessionRegistrationResult(registeredChild, spawned);
    }

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

    internal static string HashSelection(EffectiveRunSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ValidateSelection(selection);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selection.Snapshot.GetRawText())));
    }

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

    private static void ValidateAcceptedSelectionHash(string acceptedSelectionHash)
    {
        if (string.IsNullOrWhiteSpace(acceptedSelectionHash) ||
            acceptedSelectionHash.Length != 64 ||
            !acceptedSelectionHash.All(Uri.IsHexDigit))
            throw new CoordinationException(
                "accepted_run_selection_invalid", StatusCodes.Status400BadRequest);
    }

    private static void ValidateChildLimits(int maxChildren, int maxConcurrentChildren)
    {
        if (maxChildren is < 0 or > 100 || maxConcurrentChildren is < 1 or > 32)
            throw new CoordinationException("run_child_limit_invalid", StatusCodes.Status502BadGateway);
    }

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

    private static string HashSpawnRequest(SpawnSessionRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(request, JsonOptions))));

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

    public async Task<SessionStatusSnapshot> ReadSessionStatusAsync(
        CoordinationActor actor,
        SessionIdentity identity,
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
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        var (activity, activityUnavailableCode) = ToActivity(session.TurnState);
        var interruptionIntent = await ReadInterruptionIntentAsync(
            connection, transaction, identity, session.ExecutionFence, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SessionStatusSnapshot(
            identity,
            session.ParentSessionId,
            session.RootSessionId,
            ParseSessionKind(session.NodeKind),
            session.Detached,
            activity,
            activityUnavailableCode,
            ParseLifecycle(session.LifecycleState),
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
                run.ExecutionReference));
    }

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
            run.ExecutionReference);
    }

    public async Task<SessionRuntimeOwnerState> ReadRuntimeOwnerStateAsync(
        CoordinationActor actor,
        SessionIdentity identity,
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
        if (session.NodeKind != "child_work" ||
            session.WorkPlanItemId is null ||
            session.TurnState != "active" ||
            session.LogicalTurnOrdinal < 1 ||
            session.StateVersion < 1)
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
            CreateRuntimeTurnId(identity, run.Fence, session.LogicalTurnOrdinal));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

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

    private static (CoordinationActivityState Activity, string? UnavailableCode) ToActivity(string turnState) =>
        turnState switch
        {
            "presenting" or "active" => (CoordinationActivityState.Busy, null),
            "idle" or "blocked" or "completed" => (CoordinationActivityState.Idle, null),
            "failed" => (CoordinationActivityState.Unknown, "logical_turn_failed"),
            "indeterminate" => (CoordinationActivityState.Unknown, "logical_turn_indeterminate"),
            _ => (CoordinationActivityState.Unknown, "coordination_turn_state_unavailable")
        };

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

    private static void ValidateTreeCommandRequest(SessionTreeCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CoordinationIdentity.ValidateIdentity(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (request.ExecutionFence < 1 || request.IdempotencyKey.Length > 128)
            throw new CoordinationException("session_tree_command_invalid", StatusCodes.Status400BadRequest);
    }

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
            outbound.Recipient.RunId != sender.RunId ||
            outbound.Sender.SessionId == outbound.Recipient.SessionId)
            throw new CoordinationException("owner_message_invalid", StatusCodes.Status400BadRequest);

        var persistedOutbound = await ReadOutboundMessageAsync(
            outbound.OwnerMessageId, cancellationToken).ConfigureAwait(false);
        if (!SameOutboundMessage(persistedOutbound, outbound))
            throw new CoordinationException("owner_message_mismatch", StatusCodes.Status409Conflict);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, sender.ProjectId, sender.RunId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var stored = await ReadMessageAsync(
            connection, transaction, outbound.OwnerMessageId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null || stored.ProjectId != sender.ProjectId || stored.RunId != sender.RunId ||
            stored.SenderSessionId != sender.SessionId ||
            stored.RecipientSessionId != outbound.Recipient.SessionId ||
            stored.ThreadId != outbound.ThreadId ||
            stored.WriterIssuer != actor.Issuer || stored.WriterSubject != actor.Subject ||
            stored.Status is not ("pending" or "admitted"))
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

    public Task<bool> AcknowledgeOutboundAsync(
        OutboxDelivery delivery,
        CancellationToken cancellationToken) =>
        _outbox.AcknowledgeAsync(
            delivery.Event.Message.Id, delivery.LeaseToken, cancellationToken);

    public async Task<CoordinationMessageResult> SendMessageAsync(
        CoordinationActor actor,
        SessionIdentity sender,
        CoordinationMessageRequest request,
        CancellationToken cancellationToken)
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
        var target = await ReadSessionAsync(connection, transaction, recipient, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        RequireCurrentActiveSession(target, run);

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
            connection, transaction, request.OwnerMessageId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (stored is null || request.OwnerMessageId != receipt.OwnerMessageId ||
            stored.WriterIssuer != actor.Issuer || stored.WriterSubject != actor.Subject ||
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
        if (session.ExecutionFence != expectedFence || expectedFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
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
        if (session.ExecutionFence != executionFence || executionFence != run.Fence ||
            session.TurnState != "presenting" ||
            session.TurnBoundaryRequestVersion != requestStateVersion ||
            presentingStateVersion <= requestStateVersion ||
            session.StateVersion < presentingStateVersion)
            throw new CoordinationException("turn_boundary_conflict", StatusCodes.Status409Conflict);

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

        if (request.ExecutionFence != run.Fence ||
            request.ExpectedRunStateVersion != run.StateVersion ||
            session.ExecutionFence != run.Fence ||
            request.ExpectedSessionStateVersion != session.StateVersion)
            throw new CoordinationException("execution_failure_stale", StatusCodes.Status409Conflict);
        if (run.ExecutionState is "failed" or "indeterminate" or "completed" ||
            session.LifecycleState != "active" ||
            session.TurnState is not ("active" or "presenting"))
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

    public async Task<RunExecutionTransitionResult> RecoverRunExecutionAsync(
        CoordinationActor actor,
        string projectId,
        string runId,
        RecoverRunExecutionRequest request,
        CancellationToken cancellationToken,
        AuthorizedRunSelection? selection = null)
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
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }

        if (request.ExecutionFence != run.Fence || request.ExpectedRunStateVersion != run.StateVersion ||
            root.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_recovery_stale", StatusCodes.Status409Conflict);
        if (run.ExecutionState is not ("failed" or "indeterminate") ||
            run.ExecutionCauseCode is null || run.ExecutionReference is null)
            throw new CoordinationException("execution_recovery_conflict", StatusCodes.Status409Conflict);

        var previousFence = run.Fence;
        var expectedSessionCount = await ReadRunSessionCountAtFenceAsync(
            connection, transaction, projectId, runId, previousFence, cancellationToken)
            .ConfigureAwait(false);
        var nextFence = checked(previousFence + 1);
        var operationId = Guid.NewGuid();
        var fencedSessions = ImmutableArray.CreateBuilder<SessionExecutionFenceChange>();
        var rootStateVersion = 0L;
        await using (var updateSessions = new NpgsqlCommand($"""
            UPDATE {_sessions}
            SET execution_fence = @nextFence,
                turn_state = CASE WHEN turn_state IN ('failed', 'indeterminate') THEN 'idle' ELSE turn_state END,
                state_version = state_version + 1,
                turn_boundary_request_version = CASE
                    WHEN turn_state IN ('failed', 'indeterminate') THEN NULL
                    ELSE turn_boundary_request_version END,
                turn_boundary_result = CASE
                    WHEN turn_state IN ('failed', 'indeterminate') THEN NULL
                    ELSE turn_boundary_result END
            WHERE project_id = @project AND run_id = @run AND execution_fence = @previousFence
            RETURNING session_id, turn_state, state_version
            """, connection, transaction))
        {
            AddRunScope(updateSessions, projectId, runId);
            updateSessions.Parameters.AddWithValue("previousFence", NpgsqlDbType.Bigint, previousFence);
            updateSessions.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, nextFence);
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
                execution_state = 'idle',
                execution_cause_code = NULL,
                execution_reference = NULL,
                state_version = state_version + 1,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run
                AND execution_fence = @previousFence AND state_version = @runVersion
                AND execution_state IN ('failed', 'indeterminate')
            RETURNING state_version
            """, connection, transaction))
        {
            AddRunScope(updateRun, projectId, runId);
            updateRun.Parameters.AddWithValue("previousFence", NpgsqlDbType.Bigint, previousFence);
            updateRun.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, nextFence);
            updateRun.Parameters.AddWithValue("runVersion", NpgsqlDbType.Bigint, run.StateVersion);
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
            "execution.recovered",
            cancellationToken).ConfigureAwait(false);

        var result = new RunExecutionTransitionResult(
            operationId,
            identity,
            run.ExecutionState,
            "idle",
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
            fencedSessions.ToImmutable());
        await SaveRunExecutionOperationAsync(
            connection, transaction, actor, "recovery", request.IdempotencyKey, requestHash, result,
            cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(new
        {
            result,
            runtimeEffectsState = "unavailable",
            runtimeEffectsUnavailableCode = "runtime_effect_owner_unavailable",
            physicalEffectsReplayed = false
        }, JsonOptions);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            operationId,
            $"coordination/{projectId}/{runId}/execution",
            $"recovery:{actor.Issuer}:{actor.Subject}:{request.IdempotencyKey}",
            "orchestrator.run.execution_recovered",
            EventVersion,
            payload,
            _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

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
        CancellationToken cancellationToken)
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
            cancellationToken);
    }

    public async Task<TurnBoundaryResult> FinishTurnAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        FinishTurnRequest request,
        CancellationToken cancellationToken)
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
        if (request.Completion == LogicalTurnCompletion.Idle)
            await EnqueueIdleNotificationsAsync(
                connection,
                transaction,
                identity,
                request.ExecutionFence,
                result.LogicalTurnOrdinal,
                result.StateVersion,
                cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

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

        var notifications = await ReadParentNotificationsAsync(
            connection, transaction, parent, limit, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return notifications;
    }

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

    private async Task SaveRunExecutionOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        string operationKind,
        string idempotencyKey,
        string requestHash,
        RunExecutionTransitionResult result,
        CancellationToken cancellationToken)
    {
        var resultJson = JsonSerializer.SerializeToElement(result, JsonOptions);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_executionOperations}
                (project_id, run_id, operation_id, session_id, actor_issuer, actor_subject,
                 operation_kind, idempotency_key, request_hash, result)
            VALUES (@project, @run, @operation, @session, @issuer, @subject,
                @kind, @key, @hash, @result)
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
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string HashRunExecutionOperation(string operationKind, SessionIdentity identity, object request)
    {
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new { operationKind, identity, request }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(requestBytes));
    }

    private static void ValidateRunFailureRequest(ReportRunFailureRequest request)
    {
        if (request.ExecutionFence < 1 || request.ExpectedRunStateVersion < 1 ||
            request.ExpectedSessionStateVersion < 1 || !Enum.IsDefined(request.State))
            throw new CoordinationException("execution_failure_invalid", StatusCodes.Status400BadRequest);
        ValidateExecutionOperationIdentity(request.IdempotencyKey, request.CauseCode, request.Reference);
    }

    private static void ValidateRunRecoveryRequest(RecoverRunExecutionRequest request)
    {
        if (request.ExecutionFence < 1 || request.ExpectedRunStateVersion < 1)
            throw new CoordinationException("execution_recovery_invalid", StatusCodes.Status400BadRequest);
        ValidateExecutionOperationIdentity(request.IdempotencyKey, request.CauseCode, request.Reference);
    }

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

    private async Task<AcceptedRunRow> ReadAcceptedRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT accepted_selection_hash, accepted_by_issuer, accepted_by_subject,
                execution_fence, logical_turn_ordinal, execution_state, state_version, tenant_id,
                execution_cause_code, execution_reference
            FROM {_runs}
            WHERE project_id = @project AND run_id = @run
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
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

    private static CoordinationLifecycleState ParseLifecycle(string value) => value switch
    {
        "active" => CoordinationLifecycleState.Active,
        "cancelled" => CoordinationLifecycleState.Cancelled,
        "completed" => CoordinationLifecycleState.Completed,
        "archived" => CoordinationLifecycleState.Archived,
        _ => throw new CoordinationException(
            "session_tree_state_unavailable", StatusCodes.Status503ServiceUnavailable)
    };

    private static string ToDatabaseSessionKind(CoordinationSessionKind kind) => kind switch
    {
        CoordinationSessionKind.ChildWork => "child_work",
        CoordinationSessionKind.Scribe => "scribe",
        CoordinationSessionKind.OperatorChat => "operator_chat",
        CoordinationSessionKind.ChildRun => "child_run",
        _ => throw new CoordinationException(
            "session_node_kind_invalid", StatusCodes.Status400BadRequest)
    };

    private static IdleNotificationMode ParseIdleMode(string value) => value switch
    {
        "once" => IdleNotificationMode.Once,
        "always" => IdleNotificationMode.Always,
        _ => throw new CoordinationException(
            "idle_subscription_unavailable", StatusCodes.Status503ServiceUnavailable)
    };

    private static string ToDatabaseIdleMode(IdleNotificationMode mode) => mode switch
    {
        IdleNotificationMode.Once => "once",
        IdleNotificationMode.Always => "always",
        _ => throw new CoordinationException(
            "idle_subscription_invalid", StatusCodes.Status400BadRequest)
    };

    private static void RequireWriter(SessionRow session, CoordinationActor actor)
    {
        if (session.WriterIssuer != actor.Issuer || session.WriterSubject != actor.Subject)
            throw new CoordinationException("session_writer_mismatch", StatusCodes.Status403Forbidden);
    }

    private static void RequireRunOwner(AcceptedRunRow run, CoordinationActor actor)
    {
        if (run.AcceptedIssuer != actor.Issuer || run.AcceptedSubject != actor.Subject)
            throw new CoordinationException("run_owner_mismatch", StatusCodes.Status403Forbidden);
    }

    private static void RequireCurrentActiveSession(SessionRow session, AcceptedRunRow run)
    {
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);
        if (run.ExecutionState is "failed" or "indeterminate")
            throw new CoordinationException("execution_recovery_required", StatusCodes.Status409Conflict);
        if (session.LifecycleState != "active")
            throw new CoordinationException("session_unavailable", StatusCodes.Status409Conflict);
    }

    private static void ValidateSelection(EffectiveRunSelection selection)
    {
        if (selection.ProjectRevision < 1 || selection.ProjectConfigurationRevision < 1 ||
            selection.PlatformRuntimeRevision < 1 || string.IsNullOrWhiteSpace(selection.ContextRevision) ||
            selection.Snapshot.ValueKind != JsonValueKind.Object)
            throw new CoordinationException("accepted_run_selection_invalid", StatusCodes.Status400BadRequest);
        CoordinationIdentity.ValidateIdentity(selection.ProjectId, "project_id");
        CoordinationIdentity.ValidateIdentity(selection.RunId, "run_id");
    }

    private static void AddRunScope(NpgsqlCommand command, string projectId, string runId)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
    }

    private static string CreateRuntimeTurnId(
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

    private static void ValidateSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || schema.Length > 63 ||
            schema[0] is < 'a' or > 'z' ||
            schema.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_') ||
            schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
    }

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

    private sealed record SessionRegistrationResult(
        RegisteredChild Registered,
        SpawnedSession? Spawned);

    private sealed record IdleSubscriptionRow(
        Guid SubscriptionId,
        string SubscriberSessionId,
        IdleNotificationMode Mode);

    private sealed record ExistingMessage(
        Guid MessageId,
        string RecipientSessionId,
        string Status,
        string? RequestId,
        bool CanonicalRequestMatches);

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
