using System.Text.RegularExpressions;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

public static class CoordinationOwnerMigrator
{
    private static readonly Regex SchemaPattern = new(
        "^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);

    public static async Task MigrateAsync(
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ValidateSchema(schema);
        await new PostgresOutbox(dataSource, schema).InitializeAsync(cancellationToken);

        var quotedSchema = Quote(schema);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var advisoryLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.coordination.migrate'), hashtext(@schema))",
            connection, transaction))
        {
            advisoryLock.Parameters.AddWithValue("schema", NpgsqlDbType.Text, schema);
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var createSchema = new NpgsqlCommand(
            $"CREATE SCHEMA IF NOT EXISTS {quotedSchema}", connection, transaction))
            await createSchema.ExecuteNonQueryAsync(cancellationToken);
        await using (var createVersions = new NpgsqlCommand($"""
            CREATE TABLE IF NOT EXISTS {quotedSchema}.coordination_schema_migrations (
                version integer PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            )
            """, connection, transaction))
            await createVersions.ExecuteNonQueryAsync(cancellationToken);

        var applied = new List<int>();
        await using (var query = new NpgsqlCommand(
            $"SELECT version FROM {quotedSchema}.coordination_schema_migrations", connection, transaction))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                applied.Add(reader.GetInt32(0));

        if (applied.Any(version => version != 1))
            throw new InvalidOperationException("Unsupported Orchestrator coordination schema version.");
        if (!applied.Contains(1))
        {
            await using var resource = typeof(CoordinationOwnerMigrator).Assembly.GetManifestResourceStream(
                "Agentweaver.Orchestrator.Migrations.001_coordination_owner.sql")
                ?? throw new InvalidOperationException("The coordination owner migration resource is missing.");
            using var text = new StreamReader(resource);
            var sql = (await text.ReadToEndAsync(cancellationToken))
                .Replace("{schema}", quotedSchema, StringComparison.Ordinal);
            await using (var migration = new NpgsqlCommand(sql, connection, transaction))
                await migration.ExecuteNonQueryAsync(cancellationToken);
            await using (var record = new NpgsqlCommand(
                $"INSERT INTO {quotedSchema}.coordination_schema_migrations (version) VALUES (1)",
                connection, transaction))
                await record.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task VerifyAsync(
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ValidateSchema(schema);
        var quotedSchema = Quote(schema);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 1),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version NOT IN (1)),
                (SELECT count(*) FROM {quotedSchema}.outbox_schema_migrations WHERE version = 1),
                (SELECT count(*) FROM {quotedSchema}.outbox_schema_migrations WHERE version = 2),
                (SELECT count(*) FROM {quotedSchema}.outbox_schema_migrations WHERE version NOT IN (1, 2)),
                to_regclass(@runs) IS NOT NULL,
                to_regclass(@sessions) IS NOT NULL,
                to_regclass(@messages) IS NOT NULL,
                to_regclass(@requests) IS NOT NULL,
                to_regclass(@notifications) IS NOT NULL,
                to_regclass(@outbox) IS NOT NULL,
                to_regclass(@inbox) IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("runs", NpgsqlDbType.Text, $"{schema}.accepted_runs");
        command.Parameters.AddWithValue("sessions", NpgsqlDbType.Text, $"{schema}.coordination_sessions");
        command.Parameters.AddWithValue("messages", NpgsqlDbType.Text, $"{schema}.coordination_messages");
        command.Parameters.AddWithValue("requests", NpgsqlDbType.Text, $"{schema}.coordination_requests");
        command.Parameters.AddWithValue("notifications", NpgsqlDbType.Text, $"{schema}.parent_notifications");
        command.Parameters.AddWithValue("outbox", NpgsqlDbType.Text, $"{schema}.outbox_events");
        command.Parameters.AddWithValue("inbox", NpgsqlDbType.Text, $"{schema}.consumer_inbox_receipts");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt64(0) != 1 || reader.GetInt64(1) != 0 ||
            reader.GetInt64(2) != 1 || reader.GetInt64(3) != 1 || reader.GetInt64(4) != 0 ||
            Enumerable.Range(5, 7).Any(column => !reader.GetBoolean(column)))
            throw new InvalidOperationException(
                "Orchestrator coordination schema is not current; run the explicit --migrate command.");
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
