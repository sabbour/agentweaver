using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentweaver.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed class RuntimeRegistrationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;
    private readonly string _heads;
    private readonly string _revisions;

    public RuntimeRegistrationStore(NpgsqlDataSource dataSource, string schema, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (schema is null ||
            !Regex.IsMatch(schema, "^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant) ||
            schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
        _dataSource = dataSource;
        _timeProvider = timeProvider;
        _heads = $"\"{schema}\".runtime_registration_heads";
        _revisions = $"\"{schema}\".runtime_registration_revisions";
    }

    // Only the authenticated owner adapter may supply this server-derived binding.
    internal async Task<RuntimeRegistration> RegisterAsync(
        RuntimeBinding serverDerivedBinding, DateTimeOffset ownerBoundExpiresAt,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null)
    {
        var expiresAt = new DateTimeOffset(
            ownerBoundExpiresAt.UtcTicks - ownerBoundExpiresAt.UtcTicks % 10, TimeSpan.Zero);
        var candidate = new RuntimeRegistration(
            Guid.NewGuid(), 1, serverDerivedBinding, RuntimeRegistrationState.Active, expiresAt);
        RuntimeContractValidation.Validate(candidate);
        RequireAvailable(candidate);
        var bindingJson = JsonSerializer.Serialize(serverDerivedBinding, JsonOptions);
        var bindingHash = RuntimeContractValidation.Hash(
            JsonSerializer.SerializeToUtf8Bytes(serverDerivedBinding, JsonOptions));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int inserted;
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_heads}(runtime_instance_id, binding_hash, current_revision)
            VALUES (@runtime, @hash, 1)
            ON CONFLICT (binding_hash) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, candidate.RuntimeInstanceId);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, bindingHash);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (inserted == 1)
            await InsertRevisionAsync(connection, transaction, candidate, bindingJson, bindingHash, cancellationToken);
        RuntimeRegistration registered;
        await using (var read = new NpgsqlCommand($"""
            SELECT revision.runtime_instance_id, revision.revision, revision.binding_json,
                   revision.binding_hash, revision.state, revision.expires_at
            FROM {_heads} AS head
            JOIN {_revisions} AS revision
              ON revision.runtime_instance_id = head.runtime_instance_id
             AND revision.revision = head.current_revision
            WHERE head.binding_hash = @hash
            FOR UPDATE OF head
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("hash", NpgsqlDbType.Char, bindingHash);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("A runtime registration head has no current revision.");
            registered = ReadRegistration(reader);
        }
        if (registered.Binding != serverDerivedBinding || registered.ExpiresAt != expiresAt)
            throw new RuntimeAuthorizationException("runtime_registration_conflict");
        RequireAvailable(registered);
        if (revalidateCurrentAuthority is not null)
            await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        RequireAvailable(registered);
        await transaction.CommitAsync(cancellationToken);
        return registered;
    }

    internal async Task<RuntimeRegistration?> ReadAsync(Guid runtimeInstanceId, CancellationToken cancellationToken)
    {
        if (runtimeInstanceId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_registration_invalid");
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT revision.runtime_instance_id, revision.revision, revision.binding_json,
                   revision.binding_hash, revision.state, revision.expires_at
            FROM {_heads} AS head
            JOIN {_revisions} AS revision
              ON revision.runtime_instance_id = head.runtime_instance_id
             AND revision.revision = head.current_revision
            WHERE head.runtime_instance_id = @runtime
            """, connection);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, runtimeInstanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRegistration(reader) : null;
    }

    internal async Task<RuntimeRegistration> RevokeAsync(
        Guid runtimeInstanceId, long expectedRevision, CancellationToken cancellationToken)
    {
        if (runtimeInstanceId == Guid.Empty || expectedRevision <= 0 || expectedRevision == long.MaxValue)
            throw new RuntimeAuthorizationException("runtime_registration_invalid");
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        RuntimeRegistration current;
        string bindingJson;
        string bindingHash;
        await using (var read = new NpgsqlCommand($"""
            SELECT revision.runtime_instance_id, revision.revision, revision.binding_json,
                   revision.binding_hash, revision.state, revision.expires_at
            FROM {_heads} AS head
            JOIN {_revisions} AS revision
              ON revision.runtime_instance_id = head.runtime_instance_id
             AND revision.revision = head.current_revision
            WHERE head.runtime_instance_id = @runtime
            FOR UPDATE OF head
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, runtimeInstanceId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RuntimeAuthorizationException("runtime_registration_unknown");
            current = ReadRegistration(reader);
            bindingJson = reader.GetString(2);
            bindingHash = reader.GetString(3);
        }
        if (current.Revision != expectedRevision || current.State != RuntimeRegistrationState.Active)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
        var revoked = current with { Revision = checked(expectedRevision + 1), State = RuntimeRegistrationState.Revoked };
        await using (var advance = new NpgsqlCommand($"""
            UPDATE {_heads} SET current_revision = @next
            WHERE runtime_instance_id = @runtime AND current_revision = @expected
            """, connection, transaction))
        {
            advance.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, runtimeInstanceId);
            advance.Parameters.AddWithValue("expected", NpgsqlDbType.Bigint, expectedRevision);
            advance.Parameters.AddWithValue("next", NpgsqlDbType.Bigint, revoked.Revision);
            if (await advance.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new RuntimeAuthorizationException("runtime_registration_stale");
        }
        await InsertRevisionAsync(connection, transaction, revoked, bindingJson, bindingHash, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return revoked;
    }

    private async Task InsertRevisionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        string bindingJson, string bindingHash, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_revisions}
                (runtime_instance_id, revision, binding_json, binding_hash, state, expires_at, recorded_at)
            VALUES (@runtime, @revision, @binding, @hash, @state, @expiry, @recorded)
            """, connection, transaction);
        insert.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, registration.RuntimeInstanceId);
        insert.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, registration.Revision);
        insert.Parameters.AddWithValue("binding", NpgsqlDbType.Varchar, bindingJson);
        insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, bindingHash);
        insert.Parameters.AddWithValue("state", NpgsqlDbType.Integer, (int)registration.State);
        insert.Parameters.AddWithValue("expiry", NpgsqlDbType.TimestampTz, registration.ExpiresAt);
        insert.Parameters.AddWithValue("recorded", NpgsqlDbType.TimestampTz, _timeProvider.GetUtcNow());
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static RuntimeRegistration ReadRegistration(NpgsqlDataReader reader)
    {
        var binding = JsonSerializer.Deserialize<RuntimeBinding>(reader.GetString(2), JsonOptions)
            ?? throw new InvalidOperationException("A stored runtime binding is invalid.");
        if (RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(binding, JsonOptions)) !=
            reader.GetString(3))
            throw new InvalidOperationException("A stored runtime binding hash does not match.");
        var registration = new RuntimeRegistration(
            reader.GetGuid(0), reader.GetInt64(1), binding,
            (RuntimeRegistrationState)reader.GetInt32(4),
            reader.GetFieldValue<DateTimeOffset>(5));
        RuntimeContractValidation.Validate(registration);
        return registration;
    }

    private void RequireAvailable(RuntimeRegistration registration)
    {
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= _timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_registration_unavailable");
    }
}
