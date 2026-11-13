using System.Text.Json;
using Agentweaver.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed partial class RuntimeExecutionEvidenceLedgerStore
{
    internal async Task<CheckpointRecoverySupersessionReceipt> AppendCheckpointRecoverySupersessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CheckpointRecoverySupersessionReceipt receipt,
        CancellationToken cancellationToken)
    {
        receipt.Validate();
        await RequireCheckpointRecoverySupersessionSchemaAsync(
            connection, transaction, requireApplied: true, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(receipt, JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_table}
                (project_id, run_id, receipt_id, receipt_kind, attempt_id, payload_hash, receipt_json)
            VALUES
                (@project, @run, @receipt, 'checkpoint-recovery-supersession',
                 @attempt, @hash, @json)
            ON CONFLICT (project_id, run_id, receipt_kind, attempt_id) DO NOTHING
            """, connection, transaction))
        {
            AddReceiptParameters(insert, receipt, json);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var stored = await ReadCheckpointRecoverySupersessionAsync(
            connection, transaction, receipt.ProjectId, receipt.RunId, receipt.SupersedesOperationId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_supersession_conflict");
        if (stored != receipt)
            throw new RuntimeAuthorizationException("checkpoint_recovery_supersession_conflict");
        return stored;
    }

    internal async Task<CheckpointRecoverySupersessionReceipt?> ReadCheckpointRecoverySupersessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string runId,
        Guid supersededOperationId,
        CancellationToken cancellationToken)
    {
        await RequireCheckpointRecoverySupersessionSchemaAsync(
            connection, transaction, requireApplied: false, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_json::text
            FROM {_table}
            WHERE project_id = @project AND run_id = @run
              AND receipt_kind = 'checkpoint-recovery-supersession'
              AND attempt_id = @attempt
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, supersededOperationId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (json is null)
            return null;
        var receipt = JsonSerializer.Deserialize<CheckpointRecoverySupersessionReceipt>(json, JsonOptions)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_supersession_corrupt");
        if (receipt.SupersedesOperationId != supersededOperationId ||
            receipt.ProjectId != projectId || receipt.RunId != runId)
            throw new RuntimeAuthorizationException("checkpoint_recovery_supersession_corrupt");
        return receipt.Validate();
    }

    private static void AddReceiptParameters(
        NpgsqlCommand command,
        CheckpointRecoverySupersessionReceipt receipt,
        string json)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, receipt.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, receipt.RunId);
        command.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, receipt.ReceiptId);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, receipt.AttemptId);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Char, receipt.CanonicalPayloadHash);
        command.Parameters.AddWithValue("json", NpgsqlDbType.Jsonb, json);
    }

    private async Task RequireCheckpointRecoverySupersessionSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        bool requireApplied,
        CancellationToken cancellationToken)
    {
        await RequireLedgerAvailableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT count(*),
                   to_regclass(@supersededIndex) IS NOT NULL,
                   to_regclass(@successorIndex) IS NOT NULL
            FROM "{_schema}".coordination_schema_migrations
            WHERE version = 50
            """, connection, transaction);
        command.Parameters.AddWithValue(
            "supersededIndex", NpgsqlDbType.Text, $"{_schema}.uq_checkpoint_recovery_supersession_source");
        command.Parameters.AddWithValue(
            "successorIndex", NpgsqlDbType.Text, $"{_schema}.uq_checkpoint_recovery_supersession_successor");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);
        var applied = reader.GetInt64(0);
        if (applied == 1 && reader.GetBoolean(1) && reader.GetBoolean(2))
            return;
        if (applied != 0 || reader.GetBoolean(1) || reader.GetBoolean(2))
            throw new InvalidOperationException("The checkpoint recovery supersession migration is inconsistent.");
        if (requireApplied)
            throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);
    }
}
