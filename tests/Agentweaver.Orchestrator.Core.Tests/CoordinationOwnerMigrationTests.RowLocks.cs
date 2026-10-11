using Agentweaver.Orchestrator;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class CoordinationOwnerMigrationTests
{
    public static IEnumerable<object[]> InvalidRowLockGuards()
    {
        foreach (var (table, column) in new[]
        {
            ("coordinator_decisions", "decision_id"),
            ("maf_workflow_checkpoints", "checkpoint_id")
        })
        foreach (var change in new[] { "missing", "disabled", "wrong_function", "column_filtered", "conditional" })
            yield return new object[] { table, column, change };
    }

    [Theory]
    [MemberData(nameof(InvalidRowLockGuards))]
    public async Task StartupRejectsAnAbsentOrIncompleteRowLockGuard(string table, string column, string change)
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
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
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(sql, connection))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
    }

    [Theory]
    [InlineData("coordinator_decisions", "decision_id")]
    [InlineData("maf_workflow_checkpoints", "checkpoint_id")]
    public async Task RestrictedRoleCanInsertAndLockButCannotUpdateOrDisableTheGuard(string table, string column)
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var role = "coordination_locks_" + Guid.NewGuid().ToString("N");
        async Task ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }
        await ExecuteAsync($"""
            CREATE ROLE "{role}" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
            GRANT USAGE ON SCHEMA "{_schema}" TO "{role}";
            GRANT SELECT, INSERT ON "{_schema}"."{table}" TO "{role}";
            GRANT UPDATE ("{column}") ON "{_schema}"."{table}" TO "{role}";
            INSERT INTO "{_schema}".accepted_runs
                (project_id, run_id, accepted_selection, accepted_selection_hash,
                 accepted_by_issuer, accepted_by_subject)
            VALUES ('p', 'r', jsonb_build_object(), repeat('a', 64), 'issuer', 'actor');
            INSERT INTO "{_schema}".coordination_sessions
                (project_id, run_id, session_id, writer_issuer, writer_subject, execution_fence)
            VALUES ('p', 'r', 'root', 'issuer', 'actor', 1);
            SET LOCAL ROLE "{role}";
            """);
        var insertSql = table == "coordinator_decisions"
            ? $"""
              INSERT INTO "{_schema}".coordinator_decisions
                  (project_id, run_id, session_id, request_id, decision_id, actor_issuer, actor_subject,
                   execution_fence, state_version, action_kind, idempotency_key, command_hash, decision_state, decision)
              VALUES ('p', 'r', 'root', 'request', '00000000-0000-0000-0000-000000000001',
                      'issuer', 'actor', 1, 1, 'test', 'key', repeat('a', 64), 'accepted', jsonb_build_object())
              """
            : $"""
              INSERT INTO "{_schema}".maf_workflow_checkpoints
                  (project_id, run_id, session_id, store_name, checkpoint_id, payload,
                   cache_sdk_version, pinned_model_reference, accepted_selection_hash, execution_fence)
              VALUES ('p', 'r', 'root', 'test-store', 'checkpoint', jsonb_build_object(), '1', 'model:test', repeat('a', 64), 1)
              """;
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
        await transaction.SaveAsync("before_disable");
        var disable = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync($"""ALTER TABLE "{_schema}"."{table}" DISABLE TRIGGER "{table}_no_update" """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, disable.SqlState);
        await transaction.RollbackAsync("before_disable");
        await transaction.RollbackAsync();
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
    }
}
