using System.Text.RegularExpressions;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Knowledge;

public static class KnowledgeMigrator
{
    private static readonly Regex SchemaPattern = new(
        "^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);
    private const int CurrentSchemaVersion = 2;

    public static async Task MigrateAsync(
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ValidateSchema(schema);
        await new PostgresOutbox(dataSource, schema).InitializeAsync(cancellationToken).ConfigureAwait(false);

        var quotedSchema = QuoteSchema(schema);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.knowledge.migrate'), hashtext(@schema))",
            connection,
            transaction))
        {
            lockCommand.Parameters.AddWithValue("schema", NpgsqlDbType.Text, schema);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var migrationTable = new NpgsqlCommand($"""
            CREATE TABLE IF NOT EXISTS {quotedSchema}.knowledge_schema_migrations (
                version integer PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            )
            """, connection, transaction))
            await migrationTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var versions = new List<int>();
        await using (var command = new NpgsqlCommand(
            $"SELECT version FROM {quotedSchema}.knowledge_schema_migrations", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                versions.Add(reader.GetInt32(0));
        if (versions.Any(version => version is not (1 or 2)))
            throw new InvalidOperationException("The Knowledge database has an unsupported schema version.");

        foreach (var version in Enumerable.Range(1, CurrentSchemaVersion).Where(version => !versions.Contains(version)))
        {
            await using var resource = typeof(KnowledgeMigrator).Assembly.GetManifestResourceStream(
                version == 1
                    ? "Agentweaver.Knowledge.Migrations.001_knowledge.sql"
                    : "Agentweaver.Knowledge.Migrations.002_accepted_effect_receipts.sql")
                ?? throw new InvalidOperationException("The Knowledge migration resource is missing.");
            using var text = new StreamReader(resource);
            var sql = (await text.ReadToEndAsync(cancellationToken).ConfigureAwait(false))
                .Replace("{schema}", schema, StringComparison.Ordinal);
            await using var migration = new NpgsqlCommand(sql, connection, transaction);
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var recordVersion = new NpgsqlCommand(
                $"INSERT INTO {quotedSchema}.knowledge_schema_migrations (version) VALUES (@version)",
                connection,
                transaction);
            recordVersion.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
            await recordVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task VerifyAsync(
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ValidateSchema(schema);
        var quotedSchema = QuoteSchema(schema);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT pg_catalog.to_regnamespace(@schema) IS NOT NULL
               AND pg_catalog.to_regclass(@migrations) IS NOT NULL
               AND (SELECT count(*) FROM {quotedSchema}.knowledge_schema_migrations
                    WHERE version IN (1, 2)) = 2
               AND (SELECT count(*) FROM {quotedSchema}.knowledge_schema_migrations
                    WHERE version NOT IN (1, 2)) = 0
               AND (SELECT count(*) FROM {quotedSchema}.outbox_schema_migrations
                    WHERE version IN (1, 2)) = 2
               AND (SELECT count(*) FROM {quotedSchema}.outbox_schema_migrations
                    WHERE version NOT IN (1, 2)) = 0
               AND pg_catalog.to_regclass(@records) IS NOT NULL
               AND pg_catalog.to_regclass(@revisions) IS NOT NULL
               AND pg_catalog.to_regclass(@bindings) IS NOT NULL
               AND pg_catalog.to_regclass(@idempotency) IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("schema", NpgsqlDbType.Text, schema);
        command.Parameters.AddWithValue("migrations", NpgsqlDbType.Text, $"{schema}.knowledge_schema_migrations");
        command.Parameters.AddWithValue("records", NpgsqlDbType.Text, $"{schema}.knowledge_records");
        command.Parameters.AddWithValue("revisions", NpgsqlDbType.Text, $"{schema}.knowledge_revisions");
        command.Parameters.AddWithValue("bindings", NpgsqlDbType.Text, $"{schema}.memory_provider_bindings");
        command.Parameters.AddWithValue("idempotency", NpgsqlDbType.Text, $"{schema}.knowledge_write_idempotency");
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "The Knowledge schema is missing, incomplete, or has unsupported migrations. Run the approved Knowledge migration Job.");

        await VerifyRuntimePrivilegesAsync(connection, schema, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyRuntimePrivilegesAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT has_schema_privilege(current_user, @schema, 'USAGE')
               AND NOT has_schema_privilege(current_user, @schema, 'CREATE')
               AND has_table_privilege(current_user, @records, 'SELECT')
               AND has_table_privilege(current_user, @records, 'INSERT')
               AND has_table_privilege(current_user, @records, 'UPDATE')
               AND NOT has_table_privilege(current_user, @records, 'DELETE')
               AND NOT has_table_privilege(current_user, @records, 'TRUNCATE')
               AND has_table_privilege(current_user, @revisions, 'SELECT')
               AND has_table_privilege(current_user, @revisions, 'INSERT')
               AND NOT has_table_privilege(current_user, @revisions, 'UPDATE')
               AND NOT has_table_privilege(current_user, @revisions, 'DELETE')
               AND NOT has_table_privilege(current_user, @revisions, 'TRUNCATE')
               AND has_table_privilege(current_user, @bindings, 'SELECT')
               AND has_table_privilege(current_user, @bindings, 'INSERT')
               AND NOT has_table_privilege(current_user, @bindings, 'UPDATE')
               AND NOT has_table_privilege(current_user, @bindings, 'DELETE')
               AND has_table_privilege(current_user, @idempotency, 'SELECT')
               AND has_table_privilege(current_user, @idempotency, 'INSERT')
               AND has_table_privilege(current_user, @idempotency, 'UPDATE')
               AND NOT has_table_privilege(current_user, @idempotency, 'DELETE')
               AND has_table_privilege(current_user, @streams, 'SELECT')
               AND has_table_privilege(current_user, @streams, 'INSERT')
               AND has_table_privilege(current_user, @streams, 'UPDATE')
               AND has_table_privilege(current_user, @events, 'SELECT')
               AND has_table_privilege(current_user, @events, 'INSERT')
               AND has_table_privilege(current_user, @events, 'UPDATE')
               AND has_table_privilege(current_user, @outbox_migrations, 'SELECT')
            """, connection);
        command.Parameters.AddWithValue("schema", NpgsqlDbType.Text, schema);
        command.Parameters.AddWithValue("records", NpgsqlDbType.Text, $"{schema}.knowledge_records");
        command.Parameters.AddWithValue("revisions", NpgsqlDbType.Text, $"{schema}.knowledge_revisions");
        command.Parameters.AddWithValue("bindings", NpgsqlDbType.Text, $"{schema}.memory_provider_bindings");
        command.Parameters.AddWithValue("idempotency", NpgsqlDbType.Text, $"{schema}.knowledge_write_idempotency");
        command.Parameters.AddWithValue("streams", NpgsqlDbType.Text, $"{schema}.outbox_streams");
        command.Parameters.AddWithValue("events", NpgsqlDbType.Text, $"{schema}.outbox_events");
        command.Parameters.AddWithValue("outbox_migrations", NpgsqlDbType.Text, $"{schema}.outbox_schema_migrations");
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "The Knowledge runtime principal must have only the required DML access to its owned schema and outbox.");
    }

    private static void ValidateSchema(string schema)
    {
        if (!SchemaPattern.IsMatch(schema) || schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase PostgreSQL schema is required.", nameof(schema));
    }

    private static string QuoteSchema(string schema)
    {
        ValidateSchema(schema);
        return $"\"{schema}\"";
    }
}
