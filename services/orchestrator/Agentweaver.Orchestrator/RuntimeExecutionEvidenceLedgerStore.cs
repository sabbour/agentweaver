using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Security.Cryptography;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed class RuntimeNumericLimitDeniedException(
    string code,
    RuntimeNumericLimitKind kind,
    int limit,
    long observedCount,
    long ownerRevision,
    string sendStateHash,
    bool sendStateEmpty) : Exception(code)
{
    internal string Code { get; } = code;
    internal RuntimeNumericLimitKind Kind { get; } = kind;
    internal int Limit { get; } = limit;
    internal long ObservedCount { get; } = observedCount;
    internal long OwnerRevision { get; } = ownerRevision;
    internal string SendStateHash { get; } = sendStateHash;
    internal bool SendStateEmpty { get; } = sendStateEmpty;
}

internal sealed record RuntimeNumericLimitSendStateSnapshot(string Hash, bool IsEmpty);

internal static class RuntimeNumericLimitSendState
{
    internal static async Task<RuntimeNumericLimitSendStateSnapshot> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string projectId,
        string runId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT jsonb_build_object(
                'dispatches', COALESCE((
                    SELECT jsonb_agg(jsonb_build_array(dispatch_id, runtime_instance_id,
                        registration_revision, execution_fence, dispatch_state, owner_revision)
                        ORDER BY dispatch_id)
                    FROM {schema}.maf_execution_dispatches
                    WHERE project_id = @project AND run_id = @run AND dispatch_id = @attempt
                ), '[]'::jsonb),
                'actionGrants', COALESCE((
                    SELECT jsonb_agg(jsonb_build_array(grant_id, revision, is_current, grant_state,
                        source_request_id) ORDER BY grant_id, revision)
                    FROM {schema}.executable_action_grants
                    WHERE project_id = @project AND run_id = @run AND grant_id = @grant
                ), '[]'::jsonb),
                'toolEvents', COALESCE((
                    SELECT jsonb_agg(jsonb_build_array(id, event_type, payload)
                        ORDER BY id)
                    FROM {schema}.outbox_events
                    WHERE id = @attempt
                ), '[]'::jsonb)
            )::text,
            -- An action grant is authorization, not a send; retain it in the fingerprint but
            -- require the durable dispatch and outbox attempt rows to be absent for no-send.
            (
                NOT EXISTS (
                    SELECT 1 FROM {schema}.maf_execution_dispatches
                    WHERE project_id = @project AND run_id = @run AND dispatch_id = @attempt
                )
                AND NOT EXISTS (
                    SELECT 1 FROM {schema}.outbox_events WHERE id = @attempt
                )
            )
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, attemptId);
        command.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, attemptId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("runtime_numeric_limit_send_state_unavailable");
        var state = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(reader.GetString(0)));
        return new(state, reader.GetBoolean(1));
    }

    internal static async Task<string> HashAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema,
        string projectId, string runId, Guid attemptId, CancellationToken cancellationToken) =>
        (await ReadAsync(connection, transaction, schema, projectId, runId, attemptId, cancellationToken)
            .ConfigureAwait(false)).Hash;
}

internal static class RuntimeNumericLimitDenialIdentity
{
    internal static Guid ReceiptId(string projectId, string runId, Guid attemptId)
    {
        var hash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
            $"numeric-limit-denial\0{projectId}\0{runId}\0{attemptId:D}"));
        return Guid.ParseExact(hash[..32], "N");
    }
}

/// <summary>Stores append-only Orchestrator-owned execution evidence in the shared runtime-evidence ledger.</summary>
internal sealed class RuntimeExecutionEvidenceLedgerStore(string schema)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _schema = schema;
    private readonly string _table = $"\"{schema}\".maf_runtime_evidence_receipts";

    internal async Task<RuntimeNumericLimitDenialReceipt> AppendNumericLimitDenialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeNumericLimitDenialReceipt receipt,
        CancellationToken cancellationToken)
    {
        receipt.Validate();
        await RequireLedgerAvailableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(receipt, JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_table}
                (project_id, run_id, receipt_id, receipt_kind, attempt_id, payload_hash, receipt_json, created_at)
            VALUES
                (@project, @run, @receipt, 'numeric-limit-denial', @attempt, @hash, @json, @recorded)
            ON CONFLICT (project_id, run_id, receipt_kind, attempt_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, receipt.ProjectId);
            insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, receipt.RunId);
            insert.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, receipt.ReceiptId);
            insert.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, receipt.AttemptId);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, receipt.CanonicalPayloadHash);
            insert.Parameters.AddWithValue("json", NpgsqlDbType.Jsonb, json);
            insert.Parameters.AddWithValue("recorded", NpgsqlDbType.TimestampTz, receipt.RecordedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var storedJson = await ReadJsonAsync(connection, transaction, receipt, cancellationToken).ConfigureAwait(false);
        var stored = JsonSerializer.Deserialize<RuntimeNumericLimitDenialReceipt>(storedJson, JsonOptions)
            ?? throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        _ = stored.Validate();
        // Preserve the first receipt's timestamp while requiring the retry's substantive evidence to match.
        if (stored with { RecordedAt = receipt.RecordedAt, CanonicalPayloadHash = receipt.CanonicalPayloadHash } != receipt)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        return stored;
    }

    internal async Task<RuntimeNumericLimitDenialReceipt?> ReadNumericLimitDenialAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string projectId, string runId, Guid attemptId, CancellationToken cancellationToken)
    {
        await RequireLedgerAvailableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_json::text
            FROM {_table}
            WHERE project_id = @project AND run_id = @run
              AND receipt_kind = 'numeric-limit-denial' AND attempt_id = @attempt
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, attemptId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (json is null)
            return null;
        var receipt = JsonSerializer.Deserialize<RuntimeNumericLimitDenialReceipt>(json, JsonOptions)
            ?? throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_corrupt");
        return receipt.Validate();
    }

    internal async Task<RuntimeNumericLimitNativeNoSendReceipt> AppendNumericLimitNativeNoSendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeNumericLimitNativeNoSendReceipt receipt,
        CancellationToken cancellationToken)
    {
        receipt.Validate();
        await RequireLedgerAvailableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await RequireNumericLimitNativeNoSendSchemaAsync(
            connection, transaction, requireApplied: true, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(receipt, JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_table}
                (project_id, run_id, receipt_id, receipt_kind, attempt_id, payload_hash, receipt_json, created_at)
            VALUES
                (@project, @run, @receipt, 'numeric-limit-native-no-send', @attempt, @hash, @json, @recorded)
            ON CONFLICT (project_id, run_id, receipt_kind, attempt_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, receipt.ProjectId);
            insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, receipt.RunId);
            insert.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, receipt.ReceiptId);
            insert.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, receipt.AttemptId);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, receipt.CanonicalPayloadHash);
            insert.Parameters.AddWithValue("json", NpgsqlDbType.Jsonb, json);
            insert.Parameters.AddWithValue("recorded", NpgsqlDbType.TimestampTz, receipt.RecordedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var storedJson = await ReadNumericLimitNativeNoSendJsonAsync(
            connection, transaction, receipt.ProjectId, receipt.RunId, receipt.AttemptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
        var stored = JsonSerializer.Deserialize<RuntimeNumericLimitNativeNoSendReceipt>(storedJson, JsonOptions)
            ?? throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
        stored.Validate();
        if (stored with
        {
            RecordedAt = receipt.RecordedAt,
            CanonicalPayloadHash = receipt.CanonicalPayloadHash
        } != receipt)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
        return stored;
    }

    internal async Task<RuntimeNumericLimitNativeNoSendReceipt?> ReadNumericLimitNativeNoSendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string runId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await RequireLedgerAvailableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!await RequireNumericLimitNativeNoSendSchemaAsync(
                connection, transaction, requireApplied: false, cancellationToken).ConfigureAwait(false))
            return null;
        var json = await ReadNumericLimitNativeNoSendJsonAsync(
            connection, transaction, projectId, runId, attemptId, cancellationToken).ConfigureAwait(false);
        if (json is null)
            return null;
        var receipt = JsonSerializer.Deserialize<RuntimeNumericLimitNativeNoSendReceipt>(json, JsonOptions)
            ?? throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_corrupt");
        return receipt.Validate();
    }

    private async Task<string?> ReadNumericLimitNativeNoSendJsonAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        string runId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_json::text
            FROM {_table}
            WHERE project_id = @project AND run_id = @run
              AND receipt_kind = 'numeric-limit-native-no-send' AND attempt_id = @attempt
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, attemptId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RequireNumericLimitNativeNoSendSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        bool requireApplied,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 41),
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 39),
                to_regclass(@table) IS NOT NULL,
                EXISTS (SELECT 1 FROM pg_constraint
                    WHERE conrelid = to_regclass(@table) AND conname = 'maf_runtime_evidence_receipts_receipt_kind_check'
                      AND contype = 'c' AND convalidated AND pg_get_constraintdef(oid) = @kindCheck),
                (SELECT count(*) FROM pg_constraint
                    WHERE conrelid = to_regclass(@table) AND contype = 'c' AND convalidated),
                EXISTS (SELECT 1 FROM pg_constraint
                    WHERE conrelid = to_regclass(@table) AND contype = 'p'
                      AND pg_get_constraintdef(oid) = 'PRIMARY KEY (project_id, run_id, receipt_id)'),
                EXISTS (SELECT 1 FROM pg_constraint
                    WHERE conrelid = to_regclass(@table) AND contype = 'u'
                      AND pg_get_constraintdef(oid) =
                          'UNIQUE (project_id, run_id, receipt_kind, attempt_id)'),
                EXISTS (SELECT 1 FROM pg_constraint
                    WHERE conrelid = to_regclass(@table) AND contype = 'f' AND convalidated
                      AND confrelid = to_regclass(@acceptedRuns)
                      AND pg_get_constraintdef(oid) LIKE
                          'FOREIGN KEY (project_id, run_id) REFERENCES %(project_id, run_id)'),
                EXISTS (SELECT 1 FROM pg_trigger
                    WHERE tgrelid = to_regclass(@table) AND tgname = 'maf_runtime_evidence_receipts_immutable'
                      AND tgfoid = to_regprocedure(@mutationFunction) AND tgtype = 27
                      AND tgenabled = 'O' AND NOT tgisinternal
                      AND tgattr = ''::int2vector AND tgqual IS NULL AND tgnargs = 0),
                EXISTS (SELECT 1 FROM pg_trigger
                    WHERE tgrelid = to_regclass(@table) AND tgname = 'maf_runtime_evidence_receipts_no_truncate'
                      AND tgfoid = to_regprocedure(@mutationFunction) AND tgtype = 34
                      AND tgenabled = 'O' AND NOT tgisinternal
                      AND tgattr = ''::int2vector AND tgqual IS NULL AND tgnargs = 0)
            """, connection, transaction);
        command.Parameters.AddWithValue(
            "table", NpgsqlDbType.Text, $"{_schema}.maf_runtime_evidence_receipts");
        command.Parameters.AddWithValue(
            "acceptedRuns", NpgsqlDbType.Text, $"{_schema}.accepted_runs");
        command.Parameters.AddWithValue("kindCheck", NpgsqlDbType.Text,
            "CHECK (((receipt_kind)::text = ANY ((ARRAY['numeric-limit-denial'::character varying, 'replacement-comparison'::character varying, 'numeric-limit-native-no-send'::character varying])::text[])))");
        command.Parameters.AddWithValue(
            "mutationFunction", NpgsqlDbType.Text, $"{_schema}.reject_maf_runtime_evidence_mutation()");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The runtime-evidence ledger schema could not be verified.");
        var applied = reader.GetInt64(0);
        if (applied == 0 && !reader.GetBoolean(2) ||
            applied == 0 && requireApplied)
            throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);
        if (applied == 0)
            return false;
        if (applied != 1 || reader.GetInt64(1) != 1 || !reader.GetBoolean(2) ||
            !reader.GetBoolean(3) || reader.GetInt64(4) != 9 ||
            !reader.GetBoolean(5) || !reader.GetBoolean(6) || !reader.GetBoolean(7) ||
            !reader.GetBoolean(8) || !reader.GetBoolean(9))
            throw new InvalidOperationException("The numeric-limit no-send ledger schema is inconsistent.");
        return true;
    }

    private async Task RequireLedgerAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection, transaction);
        command.Parameters.AddWithValue("table", NpgsqlDbType.Text, _table);
        if (!(bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);
    }

    private async Task<string> ReadJsonAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeNumericLimitDenialReceipt receipt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_json::text
            FROM {_table}
            WHERE project_id = @project AND run_id = @run
              AND receipt_kind = 'numeric-limit-denial' AND attempt_id = @attempt
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, receipt.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, receipt.RunId);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, receipt.AttemptId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
    }
}
