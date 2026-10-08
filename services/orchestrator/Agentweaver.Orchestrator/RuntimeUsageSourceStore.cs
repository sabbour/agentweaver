using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed class RuntimeUsageSourceStore(
    NpgsqlDataSource dataSource, OrchestratorOptions options, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly string _sources = $"\"{options.Schema}\".runtime_sdk_sources";
    private readonly string _observations = $"\"{options.Schema}\".runtime_usage_observations";

    internal async Task<T> ExecuteLockedAsync<T>(
        Guid runtimeInstanceId, Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (runtimeInstanceId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_registration_invalid");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.runtime.usage'), hashtext(@runtime))",
            connection, transaction))
        {
            acquire.Parameters.AddWithValue("runtime", NpgsqlDbType.Text, runtimeInstanceId.ToString("D"));
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return await action(connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RuntimeSdkSourceReceipt> RegisterWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, SdkSessionFacts facts, CancellationToken cancellationToken)
    {
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, facts);
        var hash = RuntimeUsageSourceReceiptContract.HashSource(registration, facts);
        var existing = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            RequireSource(existing, registration, grant);
            if (existing.Receipt.Source != facts || existing.Receipt.CanonicalPayloadHash != hash)
                throw new RuntimeAuthorizationException("runtime_sdk_source_conflict");
            return existing.Receipt;
        }
        var receipt = new RuntimeSdkSourceReceipt(registration.RuntimeInstanceId, registration.Revision,
            grant.GrantId, facts, hash, RecordedAt());
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_sources}
                (runtime_instance_id, registration_revision, source_grant_id, configuration_hash,
                 canonical_payload_hash, receipt_json, recorded_at)
            VALUES (@runtime, @revision, @grant, @configuration, @hash, @receipt, @recorded)
            """, connection, transaction);
        insert.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, registration.RuntimeInstanceId);
        insert.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, registration.Revision);
        insert.Parameters.AddWithValue("grant", NpgsqlDbType.Uuid, grant.GrantId);
        insert.Parameters.AddWithValue("configuration", NpgsqlDbType.Char, grant.ConfigurationHash);
        AddReceipt(insert, hash, receipt, receipt.RecordedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    internal async Task<RuntimeUsageSourceReceipt> AppendWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, SdkUsageObservation observation, CancellationToken cancellationToken)
    {
        var source = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
        RequireSource(source, registration, grant);
        var usage = RuntimeUsageSourceReceiptContract.CreateUsage(registration, source.Receipt.Source, observation);
        var hash = RuntimeUsageSourceReceiptContract.Hash(registration, usage);
        await using (var read = new NpgsqlCommand(
            $"SELECT receipt_json FROM {_observations} WHERE event_id = @event", connection, transaction))
        {
            read.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, usage.EventId);
            var stored = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (stored is string json)
            {
                var previous = Deserialize<RuntimeUsageSourceReceipt>(json);
                RuntimeUsageSourceReceiptContract.Validate(previous);
                if (previous.CanonicalPayloadHash != hash)
                    throw new RuntimeAuthorizationException("runtime_usage_event_conflict");
                return previous;
            }
        }
        var receipt = new RuntimeUsageSourceReceipt(1, Guid.NewGuid(), registration, usage, hash, RecordedAt());
        RuntimeUsageSourceReceiptContract.Validate(receipt);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_observations}
                (receipt_id, runtime_instance_id, event_id, sdk_event_id, tenant_id, project_id,
                 run_id, session_id, canonical_payload_hash, receipt_json, recorded_at)
            VALUES (@id, @runtime, @event, @native, @tenant, @project, @run, @session, @hash, @receipt, @recorded)
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, receipt.ReceiptId);
        insert.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, registration.RuntimeInstanceId);
        insert.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, usage.EventId);
        insert.Parameters.AddWithValue("native", NpgsqlDbType.Uuid, Guid.ParseExact(usage.SdkEventId!, "D"));
        insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, registration.Binding.TenantId);
        insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, registration.Binding.ProjectId);
        insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, registration.Binding.RunId);
        insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, registration.Binding.SessionId);
        AddReceipt(insert, hash, receipt, receipt.RecordedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    internal async Task<RuntimeUsageSourceReceipt?> ReadReceiptAsync(
        Guid receiptId, string projectId, string runId, string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_json FROM {_observations}
            WHERE receipt_id = @id AND project_id = @project AND run_id = @run AND session_id = @session
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, receiptId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, sessionId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (json is not string stored)
            return null;
        var receipt = Deserialize<RuntimeUsageSourceReceipt>(stored);
        RuntimeUsageSourceReceiptContract.Validate(receipt);
        return receipt;
    }

    internal async Task<RuntimeSdkSourceReceipt> ReadCurrentSourceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        CancellationToken cancellationToken)
    {
        var stored = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, stored.Receipt.Source);
        if (stored.Receipt.RegistrationRevision != registration.Revision ||
            stored.Receipt.CanonicalPayloadHash != RuntimeUsageSourceReceiptContract.HashSource(
                registration, stored.Receipt.Source))
            throw new RuntimeAuthorizationException("runtime_sdk_source_conflict");
        return stored.Receipt;
    }

    private async Task<StoredSource?> ReadSourceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runtimeInstanceId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_json, configuration_hash, canonical_payload_hash, source_grant_id, registration_revision
            FROM {_sources} WHERE runtime_instance_id = @runtime
            """, connection, transaction);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, runtimeInstanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var receipt = Deserialize<RuntimeSdkSourceReceipt>(reader.GetString(0));
        if (receipt.RuntimeInstanceId != runtimeInstanceId ||
            receipt.CanonicalPayloadHash != reader.GetString(2) ||
            receipt.SourceGrantId != reader.GetGuid(3) || receipt.RegistrationRevision != reader.GetInt64(4))
            throw new InvalidOperationException("A stored native SDK source receipt is inconsistent.");
        return new(receipt, reader.GetString(1));
    }

    private static void RequireSource(
        StoredSource source, RuntimeRegistration registration, RuntimeGrantReceipt grant)
    {
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, source.Receipt.Source);
        if (source.Receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            source.Receipt.RegistrationRevision != registration.Revision ||
            source.Receipt.SourceGrantId != grant.GrantId || source.ConfigurationHash != grant.ConfigurationHash ||
            source.Receipt.CanonicalPayloadHash != RuntimeUsageSourceReceiptContract.HashSource(
                registration, source.Receipt.Source))
            throw new RuntimeAuthorizationException("runtime_sdk_source_conflict");
    }

    private DateTimeOffset RecordedAt()
    {
        var now = timeProvider.GetUtcNow();
        return new(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("A stored native runtime source receipt is invalid.");

    private static void AddReceipt<T>(NpgsqlCommand command, string hash, T receipt, DateTimeOffset recordedAt)
    {
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Char, hash);
        command.Parameters.AddWithValue("receipt", NpgsqlDbType.Varchar, JsonSerializer.Serialize(receipt, JsonOptions));
        command.Parameters.AddWithValue("recorded", NpgsqlDbType.TimestampTz, recordedAt);
    }

    private sealed record StoredSource(RuntimeSdkSourceReceipt Receipt, string ConfigurationHash);
}
