using System.Data;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed partial class CoordinationOwnerStore
{
    private sealed record RecoveryEventCursor(
        int Version, string TenantId, string ProjectId, string RunId, string SessionId,
        Guid OperationId, string SelectionHash, long After, long Watermark);

    internal async Task<CheckpointRecoveryEventPage> ReadCheckpointRecoveryEventsAsync(
        CoordinationActor actor, SessionIdentity identity, Guid operationId, AuthorizedRunSelection selection,
        int limit, string? cursor, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200 || operationId == Guid.Empty)
            throw new CoordinationException("checkpoint_recovery_events_invalid", StatusCodes.Status400BadRequest);
        if (selection.Selection.ProjectId != identity.ProjectId || selection.Selection.RunId != identity.RunId)
            throw new CoordinationException("accepted_run_not_found", StatusCodes.Status404NotFound);
        var selectionHash = HashSelection(selection.Selection);
        var stream = $"coordination/{identity.ProjectId}/{identity.RunId}/execution";
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, false, cancellationToken).ConfigureAwait(false);
        if (run.TenantId != selection.Authorization.TenantId)
            throw new CoordinationException("accepted_run_not_found", StatusCodes.Status404NotFound);
        if (!string.Equals(run.SelectionHash, selectionHash, StringComparison.Ordinal))
            throw new CoordinationException("coordinator_session_stale", StatusCodes.Status409Conflict);
        RequireRunOwner(run, actor);
        var root = await ReadSessionAsync(connection, transaction, identity, false, cancellationToken)
            .ConfigureAwait(false);
        RequireWriter(root, actor);
        if (root.ParentSessionId is not null || root.RootSessionId != identity.SessionId ||
            root.NodeKind != "coordinator")
            throw new CoordinationException("coordinator_session_unavailable", StatusCodes.Status404NotFound);
        if (root.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        await using (var operation = new NpgsqlCommand($"""
            SELECT result ? 'checkpointRecovery'
                AND jsonb_typeof(result -> 'checkpointRecovery') = 'object'
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
                AND operation_id = @operation AND operation_kind = 'recovery'
                AND actor_issuer = @issuer AND actor_subject = @subject
            """, connection, transaction))
        {
            AddRunScope(operation, identity.ProjectId, identity.RunId);
            operation.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            operation.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            operation.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            operation.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, actor.Subject);
            var supported = await operation.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (supported is null)
                throw new CoordinationException("checkpoint_recovery_operation_not_found", StatusCodes.Status404NotFound);
            if (supported is not true)
                throw new CoordinationException("checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict);
        }

        long head;
        await using (var command = new NpgsqlCommand($"""
            SELECT last_sequence FROM {_schema}.outbox_streams WHERE stream_id = @stream
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, stream);
            head = (long?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        }
        var position = cursor is null
            ? new RecoveryEventCursor(1, run.TenantId, identity.ProjectId, identity.RunId, identity.SessionId,
                operationId, selectionHash, 0, head)
            : ProjectListCursor.Decode<RecoveryEventCursor>(cursor);
        if (position.Version != 1 || position.TenantId != run.TenantId ||
            position.ProjectId != identity.ProjectId || position.RunId != identity.RunId ||
            position.SessionId != identity.SessionId || position.OperationId != operationId ||
            position.SelectionHash != selectionHash || position.After < 0 ||
            position.Watermark < 0 || position.After > position.Watermark)
            throw ProjectListCursor.Invalid();
        if (position.Watermark > head)
            throw new CoordinationException("checkpoint_recovery_events_changed", StatusCodes.Status409Conflict);

        CheckpointRecoveryEvent admission;
        await using (var command = new NpgsqlCommand($"""
            SELECT id, sequence, event_type, event_version, occurred_at, idempotency_key,
                CASE WHEN octet_length(payload::text) <= 1048576 THEN payload::text END
            FROM {_schema}.outbox_events WHERE stream_id = @stream AND id = @operation
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, stream);
            command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw CheckpointRecoveryEventProjection.Unavailable();
            admission = ParseRecoveryEvent(reader, operationId, identity, null);
        }
        if (admission.Sequence > position.Watermark)
            throw ProjectListCursor.Invalid();

        var events = new List<CheckpointRecoveryEvent>(limit + 1);
        await using (var command = new NpgsqlCommand($"""
            SELECT id, sequence, event_type, event_version, occurred_at, idempotency_key,
                CASE WHEN octet_length(payload::text) <= 1048576 THEN payload::text END
            FROM {_schema}.outbox_events
            WHERE stream_id = @stream AND sequence > @after AND sequence <= @watermark
                AND (id = @operation OR idempotency_key LIKE @prefix)
            ORDER BY sequence LIMIT @limit
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, stream);
            command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, position.After);
            command.Parameters.AddWithValue("watermark", NpgsqlDbType.Bigint, position.Watermark);
            command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
            command.Parameters.AddWithValue("prefix", NpgsqlDbType.Text, $"checkpoint-recovery:{operationId:D}:%");
            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var item = ParseRecoveryEvent(reader, operationId, identity,
                    reader.GetGuid(0) == operationId ? null : admission.Status);
                if (item.EventId != operationId && item.Sequence <= admission.Sequence)
                    throw CheckpointRecoveryEventProjection.Unavailable();
                events.Add(item);
            }
        }
        var hasMore = events.Count > limit;
        if (hasMore)
            events.RemoveAt(events.Count - 1);
        var next = hasMore ? ProjectListCursor.Encode(position with { After = events[^1].Sequence }) : null;

        try
        {
            var currentRun = await ReadAcceptedRunAsync(
                connection, transaction, identity.ProjectId, identity.RunId, true, cancellationToken).ConfigureAwait(false);
            var currentRoot = await ReadSessionAsync(connection, transaction, identity, true, cancellationToken)
                .ConfigureAwait(false);
            RequireRunOwner(currentRun, actor);
            if (currentRun.Fence != run.Fence || currentRun.TenantId != run.TenantId ||
                currentRun.SelectionHash != selectionHash || currentRoot.ParentSessionId is not null ||
                currentRoot.RootSessionId != identity.SessionId || currentRoot.NodeKind != "coordinator" ||
                currentRoot.WriterIssuer != actor.Issuer || currentRoot.WriterSubject != actor.Subject ||
                currentRoot.ExecutionFence != run.Fence)
                throw new CoordinationException("checkpoint_recovery_events_changed", StatusCodes.Status409Conflict);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            throw new CoordinationException("checkpoint_recovery_events_changed", StatusCodes.Status409Conflict);
        }
        return new(identity, operationId, position.Watermark, events, next, hasMore);
    }

    private static CheckpointRecoveryEvent ParseRecoveryEvent(
        NpgsqlDataReader reader, Guid operationId, SessionIdentity identity, CheckpointRecoveryEventStatus? admission)
    {
        if (reader.IsDBNull(6))
            throw CheckpointRecoveryEventProjection.Unavailable();
        return CheckpointRecoveryEventProjection.Parse(
            reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5), reader.GetString(6),
            operationId, identity, admission);
    }
}
