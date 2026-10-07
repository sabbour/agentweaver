using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed partial class CoordinationOwnerStore
{
    internal async Task<SessionIdentity> ReadRuntimeRootIdentityAsync(
        CoordinationActor actor, SessionIdentity identity, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(connection, transaction, identity, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        var root = await ReadRootSessionAsync(
                connection, transaction, identity.ProjectId, identity.RunId, forUpdate: false, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_owner_context_unavailable", StatusCodes.Status409Conflict);
        RequireWriter(root, actor);
        RequireCurrentActiveSession(root, run);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionIdentity(identity.ProjectId, identity.RunId, root.SessionId);
    }

    internal async Task<SessionRuntimeOwnerState> ReadRuntimeOwnerStateAsync(
        CoordinationActor actor, SessionIdentity identity, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var session = await ReadSessionAsync(connection, transaction, identity, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        RequireWriter(session, actor);
        RequireCurrentActiveSession(session, run);
        var itemId = await ReadRuntimeWorkPlanItemIdAsync(connection, transaction, identity, cancellationToken)
            .ConfigureAwait(false);
        if (session.ParentSessionId is null || itemId is null ||
            session.TurnState != "active" || session.LogicalTurnOrdinal < 1 || session.StateVersion < 1)
            throw new CoordinationException("runtime_owner_context_unavailable", StatusCodes.Status409Conflict);
        var root = await ReadRootSessionAsync(
                connection, transaction, identity.ProjectId, identity.RunId, forUpdate: false, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_owner_context_unavailable", StatusCodes.Status409Conflict);
        RequireWriter(root, actor);
        RequireCurrentActiveSession(root, run);
        var result = new SessionRuntimeOwnerState(
            root.SessionId, itemId, run.TenantId, run.SelectionHash, run.Fence,
            session.LogicalTurnOrdinal, session.StateVersion,
            CreateRuntimeTurnId(identity, run.Fence, session.LogicalTurnOrdinal));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task ValidateRuntimeAssociationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CoordinationActor actor,
        SessionIdentity parent, AcceptedRunRow run, ConfirmedWorkPlanItemAssociation association,
        CancellationToken cancellationToken)
    {
        CoordinationIdentity.ValidateIdentity(association.WorkPlanItemId, nameof(association.WorkPlanItemId));
        if (association.DecisionStateVersion < 1 || association.SelectionHash != run.SelectionHash)
            throw new CoordinationException("session_work_plan_item_unavailable", StatusCodes.Status409Conflict);
        var root = await ReadRootSessionAsync(
                connection, transaction, parent.ProjectId, parent.RunId, forUpdate: false, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_owner_context_unavailable", StatusCodes.Status409Conflict);
        RequireWriter(root, actor);
        RequireCurrentActiveSession(root, run);
        await using var decision = new NpgsqlCommand($"""
            SELECT state_version, execution_fence,
                decision -> 'envelope' ->> 'acceptedSelectionHash'
            FROM {_schema}.coordinator_decisions
            WHERE project_id = @project AND run_id = @run AND session_id = @root
            ORDER BY state_version DESC LIMIT 1
            """, connection, transaction);
        AddRunScope(decision, parent.ProjectId, parent.RunId);
        decision.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, root.SessionId);
        await using var reader = await decision.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt64(0) != association.DecisionStateVersion ||
            reader.GetInt64(1) != run.Fence || reader.IsDBNull(2) ||
            reader.GetString(2) != association.SelectionHash)
            throw new CoordinationException("session_work_plan_item_stale", StatusCodes.Status409Conflict);
    }

    private async Task<string?> ReadRuntimeWorkPlanItemIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT work_plan_item_id FROM {_sessions}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is DBNull ? null : (string?)result;
    }

    private static string CreateRuntimeTurnId(SessionIdentity identity, long executionFence, long logicalTurnOrdinal)
    {
        if (executionFence < 1 || logicalTurnOrdinal < 1)
            throw new InvalidOperationException("A runtime turn ID requires a current fence and turn ordinal.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Contract = "agentweaver.runtime-turn.v1",
            identity.ProjectId, identity.RunId, identity.SessionId,
            ExecutionFence = executionFence, LogicalTurnOrdinal = logicalTurnOrdinal
        });
        return $"turn-{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }
}
