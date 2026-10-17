using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Persistence.Postgres;

/// <summary>
/// Persists ordered outbox events and consumer inbox receipts in one PostgreSQL schema.
/// Enqueue and inbox admission accept the caller's transaction so domain writes can share
/// their commit boundary with the corresponding delivery record.
/// </summary>
public sealed class PostgresOutbox
{
    private static readonly Regex SchemaPattern = new("^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly string _events;
    private readonly string _streams;
    private readonly string _migrations;
    private readonly string _receipts;

    /// <summary>Creates an outbox adapter bound to one validated, non-system schema.</summary>
    /// <param name="dataSource">The Npgsql data source used to open connections.</param>
    /// <param name="schema">A lowercase application schema identifier.</param>
    /// <exception cref="ArgumentException">The schema is invalid or reserved.</exception>
    public PostgresOutbox(NpgsqlDataSource dataSource, string schema)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentNullException.ThrowIfNull(schema);
        if (!SchemaPattern.IsMatch(schema) || schema is "public" or "pg_catalog" or "information_schema"
            || schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved, lowercase PostgreSQL schema identifier is required.", nameof(schema));

        _schema = $"\"{schema}\"";
        _events = $"{_schema}.outbox_events";
        _streams = $"{_schema}.outbox_streams";
        _migrations = $"{_schema}.outbox_schema_migrations";
        _receipts = $"{_schema}.consumer_inbox_receipts";
    }

    /// <summary>
    /// Serializes schema initialization with a transaction-scoped advisory lock and applies
    /// the embedded migrations in order. Unknown or inconsistent recorded versions fail startup.
    /// </summary>
    /// <param name="cancellationToken">Cancels database work before the transaction commits.</param>
    /// <exception cref="InvalidOperationException">Migration resources or recorded versions are invalid.</exception>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.outbox.migrate'), hashtext(@schema))",
            connection, transaction))
        {
            command.Parameters.AddWithValue("schema", NpgsqlDbType.Text, _schema);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ExecuteAsync(connection, transaction, $"CREATE SCHEMA IF NOT EXISTS {_schema}", cancellationToken);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_migrations} (
                version integer PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            )
            """, cancellationToken);

        var versions = new List<int>();
        await using (var command = new NpgsqlCommand($"SELECT version FROM {_migrations}", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                versions.Add(reader.GetInt32(0));
        }

        if (versions.Any(version => version is not (1 or 2))
            || (versions.Contains(2) && !versions.Contains(1)))
            throw new InvalidOperationException("Unsupported outbox schema version.");

        foreach (var version in new[] { 1, 2 }.Where(version => !versions.Contains(version)))
        {
            await using var resource = typeof(PostgresOutbox).Assembly.GetManifestResourceStream(
                $"Agentweaver.Persistence.Postgres.Migrations.{version:000}_{(version == 1 ? "outbox" : "consumer_inbox")}.sql")
                ?? throw new InvalidOperationException("Persistence migration resource is missing.");
            using var text = new StreamReader(resource);
            var migration = (await text.ReadToEndAsync(cancellationToken)).Replace(
                "{schema}", _schema.Trim('"'), StringComparison.Ordinal);
            await ExecuteAsync(connection, transaction, migration, cancellationToken);
            await ExecuteAsync(connection, transaction, $"INSERT INTO {_migrations} (version) VALUES ({version})", cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts the consumer/message receipt in the caller's open transaction. A unique-key
    /// conflict is reported as a duplicate without changing the existing receipt.
    /// </summary>
    /// <param name="connection">The connection owning the transaction.</param>
    /// <param name="transaction">The transaction that must contain this receipt.</param>
    /// <param name="consumerId">The consumer identity scoped to the inbox.</param>
    /// <param name="messageId">The event identity being admitted.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>Whether the receipt was newly admitted or already existed.</returns>
    /// <exception cref="ArgumentException">The transaction does not belong to the open connection.</exception>
    public async Task<InboxAdmission> AdmitAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string consumerId, string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied open connection.", nameof(transaction));
        RequireText(consumerId, nameof(consumerId));
        RequireText(messageId, nameof(messageId));
        cancellationToken.ThrowIfCancellationRequested();

        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_receipts} (consumer_id, message_id) VALUES (@consumer, @message)
            ON CONFLICT (consumer_id, message_id) DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("consumer", NpgsqlDbType.Text, consumerId);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Text, messageId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? InboxAdmission.Admitted : InboxAdmission.Duplicate;
    }

    /// <summary>
    /// Adds an event in the caller's transaction and assigns the next stream sequence.
    /// Repeating the same event is idempotent; reusing its ID or key with different data
    /// throws <see cref="OutboxConflictException"/>.
    /// </summary>
    /// <param name="connection">The connection owning the transaction.</param>
    /// <param name="transaction">The transaction that must contain this event.</param>
    /// <param name="message">The event to persist.</param>
    /// <param name="cancellationToken">Cancels database work.</param>
    /// <returns>The stored event and its stable sequence.</returns>
    /// <exception cref="ArgumentException">The transaction or event is invalid.</exception>
    /// <exception cref="OutboxConflictException">An event identity is already bound to other data.</exception>
    public async Task<StoredOutboxEvent> EnqueueAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, OutboxEvent message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(message);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied open connection.", nameof(transaction));
        ValidateMessage(message);

        var ownedMessage = message with
        {
            Payload = message.Payload.Clone(),
            OccurredAt = NormalizeTimestamp(message.OccurredAt)
        };

        await using (var command = new NpgsqlCommand($"""
            INSERT INTO {_streams} (stream_id) VALUES (@stream)
            ON CONFLICT (stream_id) DO NOTHING
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, ownedMessage.StreamId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        long sequence;
        await using (var command = new NpgsqlCommand($"""
            UPDATE {_streams} SET last_sequence = last_sequence + 1
            WHERE stream_id = @stream RETURNING last_sequence
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, ownedMessage.StreamId);
            sequence = (long)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("The outbox stream was not created."));
        }

        int inserted;
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO {_events}
                (id, stream_id, sequence, idempotency_key, event_type, event_version, payload, occurred_at)
            VALUES (@id, @stream, @sequence, @key, @type, @version, @payload, @occurred)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, ownedMessage.Id);
            command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, ownedMessage.StreamId);
            command.Parameters.AddWithValue("sequence", NpgsqlDbType.Bigint, sequence);
            command.Parameters.AddWithValue("key", NpgsqlDbType.Text, ownedMessage.IdempotencyKey);
            command.Parameters.AddWithValue("type", NpgsqlDbType.Text, ownedMessage.EventType);
            command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, ownedMessage.EventVersion);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, ownedMessage.Payload.GetRawText());
            command.Parameters.AddWithValue("occurred", NpgsqlDbType.TimestampTz, ownedMessage.OccurredAt);
            inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (inserted == 1)
            return new StoredOutboxEvent(ownedMessage, sequence);

        // Undo the provisional sequence while holding the stream row lock, so a duplicate retry
        // does not leave a gap in the stream's committed ordering.
        await using (var command = new NpgsqlCommand($"""
            UPDATE {_streams} SET last_sequence = last_sequence - 1 WHERE stream_id = @stream
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, ownedMessage.StreamId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new NpgsqlCommand($"""
            SELECT id, stream_id, idempotency_key, event_type, event_version, payload, occurred_at,
                sequence, payload = @payload AS payload_matches
            FROM {_events} WHERE id = @id OR idempotency_key = @key
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, ownedMessage.Id);
            command.Parameters.AddWithValue("key", NpgsqlDbType.Text, ownedMessage.IdempotencyKey);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, ownedMessage.Payload.GetRawText());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            StoredOutboxEvent? existing = null;
            while (await reader.ReadAsync(cancellationToken))
            {
                var stored = ReadStored(reader);
                if (existing is not null || stored.Message.Id != ownedMessage.Id
                    || stored.Message.StreamId != ownedMessage.StreamId
                    || stored.Message.IdempotencyKey != ownedMessage.IdempotencyKey
                    || stored.Message.EventType != ownedMessage.EventType
                    || stored.Message.EventVersion != ownedMessage.EventVersion
                    || stored.Message.OccurredAt != ownedMessage.OccurredAt
                    || !reader.GetBoolean(8))
                    throw new OutboxConflictException("The event ID or idempotency key belongs to a different event.");
                existing = stored;
            }
            return existing ?? throw new InvalidOperationException("An outbox uniqueness constraint failed unexpectedly.");
        }
    }

    /// <summary>
    /// Leases eligible events for a worker, selecting only the earliest undelivered event in
    /// each stream. Row locking with skip-locked semantics lets competing workers claim separate
    /// events without blocking one another.
    /// </summary>
    /// <param name="workerId">The nonblank identity recorded on each lease.</param>
    /// <param name="batchSize">Maximum number of deliveries, from 1 through 1000.</param>
    /// <param name="leaseDuration">Lease lifetime, greater than zero and at most one hour.</param>
    /// <param name="cancellationToken">Cancels the database operation.</param>
    /// <returns>The claimed deliveries with fresh lease tokens.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The batch size or lease duration is outside its bound.</exception>
    public async Task<IReadOnlyList<OutboxDelivery>> ClaimAsync(
        string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        RequireText(workerId, nameof(workerId));
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 1000);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive and at most one hour.");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            WITH candidates AS (
                SELECT o.id
                FROM {_events} AS o
                WHERE o.delivered_at IS NULL
                  AND (o.leased_until IS NULL OR o.leased_until <= clock_timestamp())
                  AND NOT EXISTS (
                      SELECT 1 FROM {_events} AS previous
                      WHERE previous.stream_id = o.stream_id
                        AND previous.sequence < o.sequence
                        AND previous.delivered_at IS NULL
                  )
                ORDER BY o.occurred_at, o.id
                LIMIT @batch
                FOR UPDATE OF o SKIP LOCKED
            )
            UPDATE {_events} AS e
            SET lease_token = gen_random_uuid(),
                worker_id = @worker,
                leased_until = clock_timestamp() + @duration
            FROM candidates AS c
            WHERE e.id = c.id
            RETURNING e.id, e.stream_id, e.idempotency_key, e.event_type,
                e.event_version, e.payload, e.occurred_at, e.sequence,
                e.lease_token, e.worker_id, e.leased_until
            """, connection);
        command.Parameters.AddWithValue("batch", NpgsqlDbType.Integer, batchSize);
        command.Parameters.AddWithValue("worker", NpgsqlDbType.Text, workerId);
        command.Parameters.AddWithValue("duration", NpgsqlDbType.Interval, leaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var deliveries = new List<OutboxDelivery>();
        while (await reader.ReadAsync(cancellationToken))
            deliveries.Add(new OutboxDelivery(ReadStored(reader), reader.GetGuid(8), reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10)));
        return deliveries;
    }

    /// <summary>Claims one event by its ID, if it is currently eligible for delivery.</summary>
    /// <param name="workerId">The nonblank identity recorded on the lease.</param>
    /// <param name="eventId">The event to claim.</param>
    /// <param name="leaseDuration">Lease lifetime, greater than zero and at most one hour.</param>
    /// <param name="cancellationToken">Cancels the database operation.</param>
    /// <returns>The claimed event, or null when it is absent, delivered, leased, or behind another event.</returns>
    public Task<OutboxDelivery?> ClaimAsync(
        string workerId,
        Guid eventId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        ClaimOneAsync(workerId, eventId, leaseDuration, cancellationToken);

    /// <summary>Performs the transactional single-event claim used by the overload above.</summary>
    /// <param name="workerId">The nonblank identity recorded on the lease.</param>
    /// <param name="eventId">The event to claim.</param>
    /// <param name="leaseDuration">Lease lifetime, greater than zero and at most one hour.</param>
    /// <param name="cancellationToken">Cancels the database operation.</param>
    /// <returns>The claimed event, or null when it is not eligible.</returns>
    public async Task<OutboxDelivery?> ClaimOneAsync(
        string workerId,
        Guid eventId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        RequireText(workerId, nameof(workerId));
        if (eventId == Guid.Empty)
            throw new ArgumentException("An event ID is required.", nameof(eventId));
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration), "Lease duration must be positive and at most one hour.");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            WITH candidate AS (
                SELECT o.id
                FROM {_events} AS o
                WHERE o.id = @id
                    AND o.delivered_at IS NULL
                    AND (o.leased_until IS NULL OR o.leased_until <= clock_timestamp())
                    AND NOT EXISTS (
                        SELECT 1 FROM {_events} AS previous
                        WHERE previous.stream_id = o.stream_id
                            AND previous.sequence < o.sequence
                            AND previous.delivered_at IS NULL
                    )
                FOR UPDATE OF o SKIP LOCKED
            )
            UPDATE {_events} AS e
            SET lease_token = gen_random_uuid(),
                worker_id = @worker,
                leased_until = clock_timestamp() + @duration
            FROM candidate AS c
            WHERE e.id = c.id
            RETURNING e.id, e.stream_id, e.idempotency_key, e.event_type,
                e.event_version, e.payload, e.occurred_at, e.sequence,
                e.lease_token, e.worker_id, e.leased_until
            """, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, eventId);
        command.Parameters.AddWithValue("worker", NpgsqlDbType.Text, workerId);
        command.Parameters.AddWithValue("duration", NpgsqlDbType.Interval, leaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var delivery = new OutboxDelivery(
            ReadStored(reader),
            reader.GetGuid(8),
            reader.GetString(9),
            reader.GetFieldValue<DateTimeOffset>(10));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return delivery;
    }

    /// <summary>Reads one event and its delivery state without changing its lease.</summary>
    /// <param name="eventId">The event identity to look up.</param>
    /// <param name="cancellationToken">Cancels the database operation.</param>
    /// <returns>The event and delivery flag, or null when it is absent.</returns>
    public async Task<(StoredOutboxEvent Event, bool IsDelivered)?> ReadAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("An event ID is required.", nameof(eventId));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT id, stream_id, idempotency_key, event_type, event_version, payload,
                occurred_at, sequence, delivered_at IS NOT NULL
            FROM {_events}
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (ReadStored(reader), reader.GetBoolean(8))
            : null;
    }

    /// <summary>Clears an undelivered lease only when its fencing token still matches.</summary>
    /// <param name="eventId">The event whose lease should be released.</param>
    /// <param name="leaseToken">The exact token returned by the claim.</param>
    /// <param name="cancellationToken">Cancels the database operation.</param>
    /// <returns>True when this token released the lease; false when it was stale or settled.</returns>
    public async Task<bool> ReleaseAsync(
        Guid eventId,
        Guid leaseToken,
        CancellationToken cancellationToken = default)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("An event ID is required.", nameof(eventId));
        if (leaseToken == Guid.Empty) throw new ArgumentException("A lease token is required.", nameof(leaseToken));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_events}
            SET lease_token = NULL, worker_id = NULL, leased_until = NULL
            WHERE id = @id AND lease_token = @token AND delivered_at IS NULL
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, eventId);
        command.Parameters.AddWithValue("token", NpgsqlDbType.Uuid, leaseToken);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Marks an event delivered only if the supplied token still owns a live lease. The row
    /// is locked before checking expiry so time spent waiting for that lock cannot extend a lease.
    /// </summary>
    /// <param name="eventId">The event to acknowledge.</param>
    /// <param name="leaseToken">The exact token returned by the claim.</param>
    /// <param name="cancellationToken">Cancels the database operation.</param>
    /// <returns>True when acknowledgment committed; false when the token or lease is no longer valid.</returns>
    public async Task<bool> AcknowledgeAsync(
        Guid eventId, Guid leaseToken, CancellationToken cancellationToken = default)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("An event ID is required.", nameof(eventId));
        if (leaseToken == Guid.Empty) throw new ArgumentException("A lease token is required.", nameof(leaseToken));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Check expiry only after acquiring the lock; a lock wait can outlive a valid lease.
        await using (var leaseLock = new NpgsqlCommand(
            $"SELECT id FROM {_events} WHERE id = @id FOR UPDATE", connection, transaction))
        {
            leaseLock.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, eventId);
            if (await leaseLock.ExecuteScalarAsync(cancellationToken) is null)
                return false;
        }
        await using var command = new NpgsqlCommand($"""
            UPDATE {_events}
            SET delivered_at = clock_timestamp()
            WHERE id = @id AND lease_token = @token
                AND leased_until > clock_timestamp() AND delivered_at IS NULL
            """, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, eventId);
        command.Parameters.AddWithValue("token", NpgsqlDbType.Uuid, leaseToken);
        var acknowledged = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return acknowledged;
    }

    /// <summary>Reads the shared event columns and clones JSON before disposing its document.</summary>
    private static StoredOutboxEvent ReadStored(NpgsqlDataReader reader)
    {
        using var document = JsonDocument.Parse(reader.GetString(5));
        return new StoredOutboxEvent(
            new OutboxEvent(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), document.RootElement.Clone(),
                reader.GetFieldValue<DateTimeOffset>(6)),
            reader.GetInt64(7));
    }

    /// <summary>Normalizes event time to UTC and PostgreSQL's microsecond precision.</summary>
    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    /// <summary>Enforces nonempty identities, positive versions, object payloads, and event time.</summary>
    private static void ValidateMessage(OutboxEvent message)
    {
        if (message.Id == Guid.Empty) throw new ArgumentException("An event ID is required.", nameof(message));
        RequireText(message.StreamId, nameof(message.StreamId));
        RequireText(message.IdempotencyKey, nameof(message.IdempotencyKey));
        RequireText(message.EventType, nameof(message.EventType));
        if (message.EventVersion <= 0) throw new ArgumentOutOfRangeException(nameof(message), "Event version must be positive.");
        try
        {
            if (message.Payload.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Payload must be a valid JSON object.", nameof(message));
        }
        catch (ObjectDisposedException exception)
        {
            throw new ArgumentException("Payload must belong to a live JSON document.", nameof(message), exception);
        }
        if (message.OccurredAt == default)
            throw new ArgumentException("An occurrence timestamp is required.", nameof(message));
    }

    private static void RequireText(string value, string name)
    {
        if (value is null) throw new ArgumentNullException(name);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-blank value is required.", name);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
