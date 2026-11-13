using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed partial class RuntimeExecutionEvidenceLedgerStore
{
    internal async Task<NativeChildRebindReceipt> AppendNativeChildRebindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NativeChildRebindReceipt receipt,
        CancellationToken cancellationToken)
    {
        receipt.Validate();
        await RequireLedgerAvailableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        _ = await RequireNativeChildRebindSchemaAsync(
            connection, transaction, requireApplied: true, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(receipt, JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_table}
                (project_id, run_id, receipt_id, receipt_kind, attempt_id, payload_hash, receipt_json)
            VALUES
                (@project, @run, @receipt, 'native-child-rebind', @attempt, @hash, @json)
            ON CONFLICT (project_id, run_id, receipt_kind, attempt_id) DO NOTHING
            """, connection, transaction))
        {
            AddNativeChildRebindParameters(insert, receipt, json);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var stored = await ReadNativeChildRebindAsync(
            connection, transaction, receipt.ProjectId, receipt.RunId, receipt.AttemptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_conflict");
        if (stored.ReceiptId != receipt.ReceiptId ||
            stored.AttemptId != receipt.AttemptId ||
            stored.CanonicalPayloadHash != receipt.CanonicalPayloadHash)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_conflict");
        return stored;
    }

    internal async Task<ImmutableArray<NativeChildRebindReceipt>> ReadNativeChildRebindsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string runId,
        Guid dispatchId,
        CancellationToken cancellationToken)
    {
        await RequireLedgerAvailableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!await RequireNativeChildRebindSchemaAsync(
                connection, transaction, requireApplied: false, cancellationToken).ConfigureAwait(false))
            return [];

        await using var command = new NpgsqlCommand($"""
            SELECT receipt_id, attempt_id, payload_hash, receipt_json::text
            FROM {_table}
            WHERE project_id = @project AND run_id = @run
              AND receipt_kind = 'native-child-rebind'
              AND receipt_json->>'dispatchId' = @dispatch
            ORDER BY (receipt_json->>'destinationFence')::bigint, receipt_id
            LIMIT 513
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("dispatch", NpgsqlDbType.Text, dispatchId.ToString("D"));
        var receipts = ImmutableArray.CreateBuilder<NativeChildRebindReceipt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (receipts.Count == NativeChildRebindReceipt.MaximumLineageEdges)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
            var receipt = JsonSerializer.Deserialize<NativeChildRebindReceipt>(reader.GetString(3), JsonOptions)
                ?? throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
            if (receipt.ReceiptId != reader.GetGuid(0) ||
                receipt.AttemptId != reader.GetGuid(1) ||
                receipt.CanonicalPayloadHash != reader.GetString(2).Trim() ||
                receipt.ProjectId != projectId || receipt.RunId != runId ||
                receipt.DispatchId != dispatchId)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
            receipts.Add(receipt.Validate());
        }
        return NativeChildRebindReceipt.ValidateChain(receipts.ToImmutable(), dispatchId);
    }

    private async Task<NativeChildRebindReceipt?> ReadNativeChildRebindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string runId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_id, attempt_id, payload_hash, receipt_json::text
            FROM {_table}
            WHERE project_id = @project AND run_id = @run
              AND receipt_kind = 'native-child-rebind'
              AND attempt_id = @attempt
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, attemptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var receipt = JsonSerializer.Deserialize<NativeChildRebindReceipt>(reader.GetString(3), JsonOptions)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        if (receipt.ReceiptId != reader.GetGuid(0) ||
            receipt.AttemptId != reader.GetGuid(1) ||
            receipt.CanonicalPayloadHash != reader.GetString(2).Trim() ||
            receipt.ProjectId != projectId || receipt.RunId != runId ||
            receipt.AttemptId != attemptId)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        return receipt.Validate();
    }

    internal async Task<bool> RequireNativeChildRebindSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        bool requireApplied,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 51),
                   to_regclass(@table) IS NOT NULL,
                   EXISTS (SELECT 1 FROM pg_constraint
                     WHERE conrelid = to_regclass(@table)
                       AND conname = 'maf_runtime_evidence_receipts_receipt_kind_check'
                       AND contype = 'c' AND convalidated
                       AND pg_get_constraintdef(oid) = @kindCheck),
                   EXISTS (SELECT 1 FROM pg_constraint
                     WHERE conrelid = to_regclass(@table)
                       AND conname = 'maf_runtime_evidence_native_child_rebind_fields_check'
                       AND contype = 'c' AND convalidated
                       AND {NativeChildRebindSchemaContract.FieldsConstraintStructure}),
                   EXISTS (
                     SELECT 1
                     FROM pg_index i
                     JOIN pg_am am ON am.oid = (SELECT relam FROM pg_class WHERE oid = i.indexrelid)
                     WHERE i.indexrelid = to_regclass(@destinationIndex)
                       AND i.indrelid = to_regclass(@table)
                       AND i.indisunique AND i.indisready AND i.indisvalid
                       AND i.indnkeyatts = 4 AND i.indnatts = 4 AND am.amname = 'btree'
                       AND regexp_replace(pg_get_indexdef(i.indexrelid, 1, true), '[[:space:]()]', '', 'g') = 'project_id'
                       AND regexp_replace(pg_get_indexdef(i.indexrelid, 2, true), '[[:space:]()]', '', 'g') = 'run_id'
                       AND regexp_replace(pg_get_indexdef(i.indexrelid, 3, true), '[[:space:]()]', '', 'g') =
                           'receipt_json->>''dispatchId''::text::uuid'
                       AND regexp_replace(pg_get_indexdef(i.indexrelid, 4, true), '[[:space:]()]', '', 'g') =
                           'receipt_json->>''destinationFence''::text::bigint'
                       AND regexp_replace(pg_get_expr(i.indpred, i.indrelid), '[[:space:]()]', '', 'g') =
                           'receipt_kind::text=''native-child-rebind''::text'),
                   EXISTS (
                     SELECT 1
                     FROM pg_index i
                     JOIN pg_am am ON am.oid = (SELECT relam FROM pg_class WHERE oid = i.indexrelid)
                     WHERE i.indexrelid = to_regclass(@priorIndex)
                       AND i.indrelid = to_regclass(@table)
                       AND i.indisunique AND i.indisready AND i.indisvalid
                       AND i.indnkeyatts = 4 AND i.indnatts = 4 AND am.amname = 'btree'
                       AND regexp_replace(pg_get_indexdef(i.indexrelid, 1, true), '[[:space:]()]', '', 'g') = 'project_id'
                       AND regexp_replace(pg_get_indexdef(i.indexrelid, 2, true), '[[:space:]()]', '', 'g') = 'run_id'
                       AND regexp_replace(pg_get_indexdef(i.indexrelid, 3, true), '[[:space:]()]', '', 'g') =
                           'receipt_json->>''dispatchId''::text::uuid'
                       AND regexp_replace(pg_get_indexdef(i.indexrelid, 4, true), '[[:space:]()]', '', 'g') =
                           'receipt_json->>''priorAuthorityKey''::text'
                       AND regexp_replace(pg_get_expr(i.indpred, i.indrelid), '[[:space:]()]', '', 'g') =
                           'receipt_kind::text=''native-child-rebind''::text')
            """, connection, transaction);
        command.Parameters.AddWithValue("schema", NpgsqlDbType.Text, _schema);
        command.Parameters.AddWithValue("table", NpgsqlDbType.Text, $"{_schema}.maf_runtime_evidence_receipts");
        command.Parameters.AddWithValue("destinationIndex", NpgsqlDbType.Text,
            $"{_schema}.uq_native_child_rebind_destination");
        command.Parameters.AddWithValue("priorIndex", NpgsqlDbType.Text,
            $"{_schema}.uq_native_child_rebind_prior");
        command.Parameters.AddWithValue("kindCheck", NpgsqlDbType.Text,
            "CHECK (((receipt_kind)::text = ANY ((ARRAY['numeric-limit-denial'::character varying, 'replacement-comparison'::character varying, 'numeric-limit-native-no-send'::character varying, 'native-root-turn-completion'::character varying, 'native-root-turn-replay'::character varying, 'checkpoint-recovery-supersession'::character varying, 'native-child-rebind'::character varying])::text[])))");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The native child rebind ledger schema could not be verified.");
        var applied = reader.GetInt64(0);
        var tableExists = reader.GetBoolean(1);
        var kindCheck = reader.GetBoolean(2);
        var fieldsCheck = reader.GetBoolean(3);
        var destinationIndex = reader.GetBoolean(4);
        var priorIndex = reader.GetBoolean(5);
        var artifactsPresent = kindCheck || fieldsCheck || destinationIndex || priorIndex;
        if (applied == 0 && tableExists && !artifactsPresent)
        {
            if (requireApplied)
                throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);
            return false;
        }
        if (applied != 1 || !tableExists || !kindCheck || !fieldsCheck ||
            !destinationIndex || !priorIndex)
            throw new InvalidOperationException("The native child rebind ledger schema is inconsistent.");
        return true;
    }

    private static void AddNativeChildRebindParameters(
        NpgsqlCommand command,
        NativeChildRebindReceipt receipt,
        string json)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, receipt.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, receipt.RunId);
        command.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, receipt.ReceiptId);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, receipt.AttemptId);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Char, receipt.CanonicalPayloadHash);
        command.Parameters.AddWithValue("json", NpgsqlDbType.Jsonb, json);
    }
}
