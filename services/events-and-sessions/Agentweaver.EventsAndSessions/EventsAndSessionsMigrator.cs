using System.Text.RegularExpressions;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.EventsAndSessions;

public static class EventsAndSessionsMigrator
{
    private const int CurrentSchemaVersion = 2;
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

        if (applied.Any(version => version is not (1 or 2)))
            throw new InvalidOperationException("Unsupported Events & Sessions schema version.");
        foreach (var version in Enumerable.Range(1, CurrentSchemaVersion).Where(version => !applied.Contains(version)))
        {
            await using var resource = typeof(EventsAndSessionsMigrator).Assembly.GetManifestResourceStream(
                version == 1
                    ? "Agentweaver.EventsAndSessions.Migrations.001_sessions_journal.sql"
                    : "Agentweaver.EventsAndSessions.Migrations.002_project_facts.sql")
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
                (SELECT count(*) FROM {quotedSchema}.sessions_schema_migrations WHERE version IN (1, 2)),
                (SELECT count(*) FROM {quotedSchema}.sessions_schema_migrations WHERE version NOT IN (1, 2)),
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
                to_regclass(@projectFacts) IS NOT NULL
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt64(0) != 2 || reader.GetInt64(1) != 0 ||
            reader.GetInt64(2) != 1 || reader.GetInt64(3) != 1 ||
            reader.GetInt64(4) != 0 ||
            Enumerable.Range(5, 10).Any(column => !reader.GetBoolean(column)))
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
