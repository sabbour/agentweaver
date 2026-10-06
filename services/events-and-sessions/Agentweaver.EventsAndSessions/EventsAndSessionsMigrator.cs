using System.Text.RegularExpressions;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.EventsAndSessions;

public static class EventsAndSessionsMigrator
{
    private const int CurrentSchemaVersion = 3;
    private static readonly Regex SchemaPattern = new(
        "^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);

    public static async Task MigrateAsync(
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken cancellationToken = default)
    {
        ValidateSchema(schema);
        ArgumentNullException.ThrowIfNull(dataSource);
        await new PostgresOutbox(dataSource, schema).InitializeAsync(cancellationToken);

        var quotedSchema = Quote(schema);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var advisoryLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.sessions.migrate'), hashtext(@schema))",
            connection, transaction))
        {
            advisoryLock.Parameters.AddWithValue("schema", NpgsqlDbType.Text, schema);
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var createSchema = new NpgsqlCommand(
            $"CREATE SCHEMA IF NOT EXISTS {quotedSchema}", connection, transaction))
            await createSchema.ExecuteNonQueryAsync(cancellationToken);

        await using (var createVersions = new NpgsqlCommand($"""
            CREATE TABLE IF NOT EXISTS {quotedSchema}.sessions_schema_migrations (
                version integer PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            )
            """, connection, transaction))
            await createVersions.ExecuteNonQueryAsync(cancellationToken);

        var applied = new List<int>();
        await using (var query = new NpgsqlCommand(
            $"SELECT version FROM {quotedSchema}.sessions_schema_migrations", connection, transaction))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                applied.Add(reader.GetInt32(0));

        if (applied.Any(version => version is < 1 or > CurrentSchemaVersion) ||
            (applied.Contains(2) && !applied.Contains(1)) ||
            (applied.Contains(3) && !applied.Contains(2)))
            throw new InvalidOperationException("Unsupported Events & Sessions schema version.");

        var legacyProjectFactsV2 = false;
        if (applied.Contains(2))
        {
            await using var layout = new NpgsqlCommand("""
                SELECT
                    to_regclass(@factStreams) IS NOT NULL,
                    to_regclass(@facts) IS NOT NULL,
                    to_regclass(@messageThreads) IS NOT NULL,
                    to_regclass(@messages) IS NOT NULL,
                    to_regclass(@messageBindings) IS NOT NULL
                """, connection, transaction);
            layout.Parameters.AddWithValue("factStreams", NpgsqlDbType.Text, $"{schema}.project_fact_streams");
            layout.Parameters.AddWithValue("facts", NpgsqlDbType.Text, $"{schema}.project_facts");
            layout.Parameters.AddWithValue("messageThreads", NpgsqlDbType.Text, $"{schema}.addressed_message_threads");
            layout.Parameters.AddWithValue("messages", NpgsqlDbType.Text, $"{schema}.addressed_messages");
            layout.Parameters.AddWithValue("messageBindings", NpgsqlDbType.Text, $"{schema}.messaging_provider_bindings");
            await using var reader = await layout.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Events & Sessions schema migration layout could not be read.");
            var hasFactStreams = reader.GetBoolean(0);
            var hasFacts = reader.GetBoolean(1);
            var hasMessageThreads = reader.GetBoolean(2);
            var hasMessages = reader.GetBoolean(3);
            var hasMessageBindings = reader.GetBoolean(4);
            var hasAnyFacts = hasFactStreams || hasFacts;
            var hasAnyMessages = hasMessageThreads || hasMessages || hasMessageBindings;
            if (hasFactStreams && hasFacts && !hasAnyMessages && !applied.Contains(3))
                legacyProjectFactsV2 = true;
            else if (!hasAnyFacts && hasMessageThreads && hasMessages && hasMessageBindings &&
                !applied.Contains(3))
                legacyProjectFactsV2 = false;
            else if (hasFactStreams && hasFacts && hasMessageThreads && hasMessages &&
                hasMessageBindings && applied.Contains(3))
                legacyProjectFactsV2 = false;
            else
                throw new InvalidOperationException(
                    "Events & Sessions schema version 2 has an unrecognized or ambiguous migration layout.");
        }

        foreach (var version in Enumerable.Range(1, CurrentSchemaVersion).Where(version => !applied.Contains(version)))
        {
            var resourceName = version switch
            {
                1 => "Agentweaver.EventsAndSessions.Migrations.001_sessions_journal.sql",
                2 => "Agentweaver.EventsAndSessions.Migrations.002_addressed_messages.sql",
                // The source candidate previously recorded project facts as v2; install the missing messages as v3.
                3 when legacyProjectFactsV2 => "Agentweaver.EventsAndSessions.Migrations.002_addressed_messages.sql",
                3 => "Agentweaver.EventsAndSessions.Migrations.003_project_facts.sql",
                _ => throw new InvalidOperationException("Unsupported Events & Sessions schema version.")
            };
            await using var resource = typeof(EventsAndSessionsMigrator).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("The Sessions migration resource is missing.");
            using var text = new StreamReader(resource);
            var sql = (await text.ReadToEndAsync(cancellationToken))
                .Replace("{schema}", quotedSchema, StringComparison.Ordinal);
            await using (var migration = new NpgsqlCommand(sql, connection, transaction))
                await migration.ExecuteNonQueryAsync(cancellationToken);
            await using (var record = new NpgsqlCommand(
                $"INSERT INTO {quotedSchema}.sessions_schema_migrations (version) VALUES (@version)",
                connection, transaction))
            {
                record.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task VerifyAsync(
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken cancellationToken = default)
    {
        ValidateSchema(schema);
        ArgumentNullException.ThrowIfNull(dataSource);
        var quotedSchema = Quote(schema);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM {quotedSchema}.sessions_schema_migrations WHERE version = 1),
                (SELECT count(*) FROM {quotedSchema}.sessions_schema_migrations WHERE version = 2),
                (SELECT count(*) FROM {quotedSchema}.sessions_schema_migrations WHERE version = 3),
                (SELECT count(*) FROM {quotedSchema}.sessions_schema_migrations WHERE version NOT IN (1, 2, 3)),
                (SELECT count(*) FROM {quotedSchema}.outbox_schema_migrations WHERE version = 1),
                (SELECT count(*) FROM {quotedSchema}.outbox_schema_migrations WHERE version = 2),
                (SELECT count(*) FROM {quotedSchema}.outbox_schema_migrations WHERE version NOT IN (1, 2)),
                to_regclass(@sessions) IS NOT NULL,
                to_regclass(@bindings) IS NOT NULL,
                to_regclass(@runStreams) IS NOT NULL,
                to_regclass(@events) IS NOT NULL,
                to_regclass(@references) IS NOT NULL,
                to_regclass(@outbox) IS NOT NULL,
                to_regclass(@streams) IS NOT NULL,
                to_regclass(@inbox) IS NOT NULL,
                to_regclass(@projectFactStreams) IS NOT NULL,
                to_regclass(@projectFacts) IS NOT NULL,
                to_regclass(@messageThreads) IS NOT NULL,
                to_regclass(@messages) IS NOT NULL,
                to_regclass(@messageBindings) IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("sessions", NpgsqlDbType.Text, $"{schema}.sessions");
        command.Parameters.AddWithValue("bindings", NpgsqlDbType.Text, $"{schema}.session_provider_bindings");
        command.Parameters.AddWithValue("runStreams", NpgsqlDbType.Text, $"{schema}.session_run_streams");
        command.Parameters.AddWithValue("events", NpgsqlDbType.Text, $"{schema}.session_events");
        command.Parameters.AddWithValue("references", NpgsqlDbType.Text, $"{schema}.session_object_references");
        command.Parameters.AddWithValue("outbox", NpgsqlDbType.Text, $"{schema}.outbox_events");
        command.Parameters.AddWithValue("streams", NpgsqlDbType.Text, $"{schema}.outbox_streams");
        command.Parameters.AddWithValue("inbox", NpgsqlDbType.Text, $"{schema}.consumer_inbox_receipts");
        command.Parameters.AddWithValue("projectFactStreams", NpgsqlDbType.Text, $"{schema}.project_fact_streams");
        command.Parameters.AddWithValue("projectFacts", NpgsqlDbType.Text, $"{schema}.project_facts");
        command.Parameters.AddWithValue("messageThreads", NpgsqlDbType.Text, $"{schema}.addressed_message_threads");
        command.Parameters.AddWithValue("messages", NpgsqlDbType.Text, $"{schema}.addressed_messages");
        command.Parameters.AddWithValue("messageBindings", NpgsqlDbType.Text, $"{schema}.messaging_provider_bindings");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt64(0) != 1 || reader.GetInt64(1) != 1 || reader.GetInt64(2) != 1 ||
            reader.GetInt64(3) != 0 || reader.GetInt64(4) != 1 || reader.GetInt64(5) != 1 ||
            reader.GetInt64(6) != 0 ||
            Enumerable.Range(7, 13).Any(column => !reader.GetBoolean(column)))
            throw new InvalidOperationException(
                "Events & Sessions schema is not current; run the explicit --migrate command.");
    }

    private static void ValidateSchema(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (!SchemaPattern.IsMatch(schema) || schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
    }

    private static string Quote(string schema) => $"\"{schema}\"";
}
