using Agentweaver.EventsAndSessions;
using Npgsql;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed partial class EventsAndSessionsMigrationTests
{
    public static IEnumerable<object[]> InvalidRowLockGuards()
    {
        foreach (var (table, column) in new[]
        {
            ("sessions", "session_id"),
            ("session_provider_bindings", "run_id"),
            ("session_fork_lineage", "target_session_id"),
            ("messaging_provider_bindings", "run_id")
        })
        foreach (var change in new[] { "missing", "disabled", "wrong_function", "column_filtered", "conditional" })
            yield return new object[] { table, column, change };
    }

    [Theory]
    [MemberData(nameof(InvalidRowLockGuards))]
    public async Task StartupRejectsAnAbsentOrIncompleteRowLockGuard(string table, string column, string change)
    {
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await EventsAndSessionsMigrator.VerifyAsync(_fixture.DataSource, _schema);
        var sql = change switch
        {
            "missing" => $"""DROP TRIGGER "{table}_no_update" ON "{_schema}"."{table}" """,
            "disabled" => $"""ALTER TABLE "{_schema}"."{table}" DISABLE TRIGGER "{table}_no_update" """,
            "wrong_function" => $"""
                CREATE FUNCTION "{_schema}".test_allow_update() RETURNS trigger LANGUAGE plpgsql
                    AS $$ BEGIN RETURN NEW; END $$;
                DROP TRIGGER "{table}_no_update" ON "{_schema}"."{table}";
                CREATE TRIGGER "{table}_no_update" BEFORE UPDATE ON "{_schema}"."{table}"
                    FOR EACH ROW EXECUTE FUNCTION "{_schema}".test_allow_update();
                """,
            "column_filtered" => $"""
                DROP TRIGGER "{table}_no_update" ON "{_schema}"."{table}";
                CREATE TRIGGER "{table}_no_update" BEFORE UPDATE OF "{column}" ON "{_schema}"."{table}"
                    FOR EACH ROW EXECUTE FUNCTION "{_schema}".reject_row_lock_record_update();
                """,
            "conditional" => $"""
                DROP TRIGGER "{table}_no_update" ON "{_schema}"."{table}";
                CREATE TRIGGER "{table}_no_update" BEFORE UPDATE ON "{_schema}"."{table}"
                    FOR EACH ROW WHEN (false) EXECUTE FUNCTION "{_schema}".reject_row_lock_record_update();
                """,
            _ => throw new ArgumentException("Unsupported row-lock guard change.")
        };
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(sql, connection))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EventsAndSessionsMigrator.VerifyAsync(_fixture.DataSource, _schema));
    }

    [Theory]
    [InlineData("sessions", "session_id")]
    [InlineData("session_provider_bindings", "run_id")]
    [InlineData("session_fork_lineage", "target_session_id")]
    [InlineData("messaging_provider_bindings", "run_id")]
    public async Task RestrictedRoleCanInsertAndLockButCannotUpdateOrDisableTheGuard(string table, string column)
    {
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var role = "session_locks_" + Guid.NewGuid().ToString("N");
        async Task ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }
        await ExecuteAsync($"""
            CREATE ROLE "{role}" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
            GRANT USAGE ON SCHEMA "{_schema}" TO "{role}";
            GRANT SELECT, INSERT ON "{_schema}".sessions, "{_schema}".session_provider_bindings,
                "{_schema}".session_fork_lineage, "{_schema}".messaging_provider_bindings TO "{role}";
            GRANT UPDATE ("{column}") ON "{_schema}"."{table}" TO "{role}";
            INSERT INTO "{_schema}".session_provider_bindings
                (project_id, run_id, provider_id, adapter_version, options_schema_version,
                 options_revision, resource_id, resource_generation, negotiated_capabilities)
            VALUES ('p', 'r', 'native', '1', 1, '1', 'resource', 1, '[]'),
                   ('p', 'runtime', 'native', '1', 1, '1', 'runtime-resource', 1, '[]');
            INSERT INTO "{_schema}".session_run_streams (project_id, run_id)
            VALUES ('p', 'r'), ('p', 'runtime');
            INSERT INTO "{_schema}".messaging_provider_bindings
                (project_id, run_id, provider_id, adapter_version, options_schema_version,
                 options_revision, resource_id, resource_generation, negotiated_capabilities)
            VALUES ('p', 'r', 'native', '1', 1, '1', 'resource', 1, '[]');
            INSERT INTO "{_schema}".sessions (project_id, run_id, session_id)
            VALUES ('p', 'r', 'source'), ('p', 'r', 'target');
            INSERT INTO "{_schema}".session_events
                (event_id, project_id, run_id, session_id, position, schema_version, event_version,
                 event_kind, occurred_at, payload, canonical_input, object_references)
            VALUES ('00000000-0000-0000-0000-000000000001', 'p', 'r', 'source', 1, 1, 1,
                    'test', clock_timestamp(), jsonb_build_object(), jsonb_build_object(), '[]');
            SET LOCAL ROLE "{role}";
            """);
        var insertSql = table switch
        {
            "sessions" => $"""INSERT INTO "{_schema}".sessions (project_id, run_id, session_id) VALUES ('p', 'r', 'runtime')""",
            "session_provider_bindings" => $"""
                INSERT INTO "{_schema}".session_provider_bindings
                    (project_id, run_id, provider_id, adapter_version, options_schema_version,
                     options_revision, resource_id, resource_generation, negotiated_capabilities)
                VALUES ('p', 'runtime', 'native', '1', 1, '1', 'runtime-resource', 1, '[]')
                """,
            "session_fork_lineage" => $"""
                INSERT INTO "{_schema}".session_fork_lineage
                    (project_id, run_id, target_session_id, source_session_id, source_event_id,
                     source_position, cursor_version, source_schema_version, source_event_version,
                     provider_binding_hash, source_cursor, actor_subject, idempotency_key, command_hash)
                VALUES ('p', 'r', 'target', 'source', '00000000-0000-0000-0000-000000000001',
                        1, 1, 1, 1, repeat('a', 64), 'cursor', 'actor', 'key', repeat('b', 64))
                """,
            "messaging_provider_bindings" => $"""
                INSERT INTO "{_schema}".messaging_provider_bindings
                    (project_id, run_id, provider_id, adapter_version, options_schema_version,
                     options_revision, resource_id, resource_generation, negotiated_capabilities)
                VALUES ('p', 'runtime', 'native', '1', 1, '1', 'runtime-resource', 1, '[]')
                """,
            _ => throw new ArgumentException("Unsupported row-lock fixture table.")
        };
        await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        foreach (var clause in new[] { "FOR UPDATE", "FOR SHARE" })
        {
            await using var read = new NpgsqlCommand(
                $"""SELECT "{column}" FROM "{_schema}"."{table}" {clause}""", connection, transaction);
            Assert.NotNull(await read.ExecuteScalarAsync());
        }
        await transaction.SaveAsync("before_update");
        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync($"""UPDATE "{_schema}"."{table}" SET "{column}" = "{column}" """));
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, update.SqlState);
        await transaction.RollbackAsync("before_update");
        if (table == "messaging_provider_bindings")
        {
            await transaction.SaveAsync("before_ungranted_update");
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync($"""
                    UPDATE "{_schema}".messaging_provider_bindings
                    SET provider_id = 'changed' WHERE run_id = 'runtime'
                    """));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            await transaction.RollbackAsync("before_ungranted_update");
        }
        await transaction.SaveAsync("before_disable");
        var disable = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync($"""ALTER TABLE "{_schema}"."{table}" DISABLE TRIGGER "{table}_no_update" """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, disable.SqlState);
        await transaction.RollbackAsync("before_disable");
        await transaction.RollbackAsync();
        await EventsAndSessionsMigrator.VerifyAsync(_fixture.DataSource, _schema);
    }
}
