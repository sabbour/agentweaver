using System.Text.Json;
using Agentweaver.P1PostgresInitializer;
using Npgsql;
using Xunit;

namespace Agentweaver.FoundationProbe.Tests;

public sealed class P1PostgresInitializerPreflightPostgresTests(ProbePostgresFixture fixture)
    : IClassFixture<ProbePostgresFixture>
{
    private static readonly string[] ServiceSchemas =
        ["projects_config", "environment", "knowledge", "events_sessions", "orchestrator"];

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task ProductionCatalogSqlAllowsOnlySafePublicTriggerFunctionsAndKeepsBoundaryExact()
    {
        await using (var maintenance = new NpgsqlConnection(fixture.ConnectionString))
        {
            await maintenance.OpenAsync();
            await ExecuteAsync(maintenance, "CREATE DATABASE agentweaver");
            await ExecuteAsync(maintenance,
                "REVOKE CONNECT, TEMPORARY ON DATABASE agentweaver FROM PUBLIC");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = "agentweaver",
        }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await CreateBrokerFixtureAsync(connection);

        var functionAcls = await ReadBrokerFunctionAclsAsync(connection);
        Assert.Contains("=X/", functionAcls, StringComparison.Ordinal);
        Assert.False((await ReadProductionPreflightAsync(connection)).ForeignPublicAuthority);
        var boundary = await ReadExistingBoundaryAsync(connection);
        InitializerChecks.AssertPreflight(await ReadProductionPreflightAsync(connection));
        InitializerChecks.AssertBoundary(boundary, await ReadExistingBoundaryAsync(connection), []);
        Assert.Equal(functionAcls, await ReadBrokerFunctionAclsAsync(connection));

        await ExecuteAsync(connection, """
            ALTER FUNCTION identity_broker.complete_secret_grant_operation_once() SECURITY DEFINER
            """);
        var changedBoundary = await ReadExistingBoundaryAsync(connection);
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertBoundary(boundary, changedBoundary, []));
        Assert.True((await ReadProductionPreflightAsync(connection)).ForeignPublicAuthority);
        await ExecuteAsync(connection, """
            ALTER FUNCTION identity_broker.complete_secret_grant_operation_once() SECURITY INVOKER
            """);
        InitializerChecks.AssertBoundary(boundary, await ReadExistingBoundaryAsync(connection), []);

        await ExecuteAsync(connection, "CREATE ROLE p1_initializer_runtime NOLOGIN");
        await ExecuteAsync(connection, "GRANT USAGE ON SCHEMA identity_broker TO p1_initializer_runtime");
        await ExecuteAsync(connection,
            "GRANT SELECT, INSERT ON identity_broker.secret_grant_operations TO p1_initializer_runtime");
        Assert.True(await RuntimeCanExecuteBrokerTriggerAsync(connection));
        await ExecuteAsync(connection, "SET ROLE p1_initializer_runtime");
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection,
                    "SELECT identity_broker.complete_secret_grant_operation_once()"));
            var createTriggerFailure = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, """
                    CREATE TRIGGER p1_runtime_trigger_attempt
                    BEFORE INSERT ON identity_broker.secret_grant_operations
                    FOR EACH ROW EXECUTE FUNCTION identity_broker.complete_secret_grant_operation_once()
                    """));
            Assert.Equal("42501", createTriggerFailure.SqlState);
        }
        finally
        {
            await ExecuteAsync(connection, "RESET ROLE");
        }

        await ExecuteAsync(connection, """
            CREATE FUNCTION identity_broker.scalar_function() RETURNS integer
            LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$
            """);
        await AssertPreflightRejectedAsync(connection, foreignAuthority: true);
        await ExecuteAsync(connection, "DROP FUNCTION identity_broker.scalar_function()");

        await ExecuteAsync(connection, """
            CREATE PROCEDURE identity_broker.no_op_procedure()
            LANGUAGE plpgsql AS $$ BEGIN NULL; END $$
            """);
        await AssertPreflightRejectedAsync(connection, foreignAuthority: true);
        await ExecuteAsync(connection, "DROP PROCEDURE identity_broker.no_op_procedure()");

        await ExecuteAsync(connection, """
            CREATE FUNCTION identity_broker.event_handler() RETURNS event_trigger
            LANGUAGE plpgsql AS $$ BEGIN RETURN; END $$
            """);
        await AssertPreflightRejectedAsync(connection, foreignAuthority: true);
        await ExecuteAsync(connection, "DROP FUNCTION identity_broker.event_handler()");

        await ExecuteAsync(connection, """
            CREATE FUNCTION identity_broker.definer_trigger() RETURNS trigger
            LANGUAGE plpgsql SECURITY DEFINER AS $$ BEGIN RETURN NEW; END $$
            """);
        await AssertPreflightRejectedAsync(connection, foreignAuthority: true);
        await ExecuteAsync(connection, "DROP FUNCTION identity_broker.definer_trigger()");

        await ExecuteAsync(connection, "SET check_function_bodies = off");
        try
        {
            await ExecuteAsync(connection, """
                CREATE FUNCTION identity_broker.argumented_trigger(value integer) RETURNS trigger
                LANGUAGE plpgsql AS $$ BEGIN RETURN NULL; END $$
                """);
        }
        finally
        {
            await ExecuteAsync(connection, "RESET check_function_bodies");
        }
        await AssertPreflightRejectedAsync(connection, foreignAuthority: true);
        await ExecuteAsync(connection, "DROP FUNCTION identity_broker.argumented_trigger(integer)");

        await ExecuteAsync(connection, """
            CREATE FUNCTION identity_broker.non_plpgsql_trigger() RETURNS trigger
            AS 'suppress_redundant_updates_trigger' LANGUAGE internal
            """);
        await ExecuteAsync(connection, """
            CREATE TRIGGER non_plpgsql_trigger
            BEFORE UPDATE ON identity_broker.secret_grant_operations
            FOR EACH ROW EXECUTE FUNCTION identity_broker.non_plpgsql_trigger()
            """);
        await AssertPreflightRejectedAsync(connection, foreignAuthority: true);
        await ExecuteAsync(connection, """
            DROP TRIGGER non_plpgsql_trigger ON identity_broker.secret_grant_operations
            """);
        await ExecuteAsync(connection, "DROP FUNCTION identity_broker.non_plpgsql_trigger()");

        var grantOptionError = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, """
                GRANT EXECUTE ON FUNCTION identity_broker.complete_secret_grant_operation_once()
                TO PUBLIC WITH GRANT OPTION
                """));
        Assert.NotEmpty(grantOptionError.SqlState);

        await ExecuteAsync(connection, "GRANT CREATE ON SCHEMA public TO PUBLIC");
        await AssertPreflightRejectedAsync(connection, foreignAuthority: true);
        await ExecuteAsync(connection, "REVOKE CREATE ON SCHEMA public FROM PUBLIC");

        await ExecuteAsync(connection,
            "GRANT SELECT ON identity_broker.secret_grant_operations TO PUBLIC");
        await AssertPreflightRejectedAsync(connection, foreignAuthority: true);
        await ExecuteAsync(connection,
            "REVOKE SELECT ON identity_broker.secret_grant_operations FROM PUBLIC");

        await ExecuteAsync(connection, "ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO PUBLIC");
        await AssertPreflightRejectedAsync(connection, incompatibleDefaults: true);
        await ExecuteAsync(connection, "ALTER DEFAULT PRIVILEGES REVOKE SELECT ON TABLES FROM PUBLIC");

        await ExecuteAsync(connection, "GRANT CREATE ON DATABASE agentweaver TO PUBLIC");
        await AssertPreflightRejectedAsync(connection, foreignAuthority: false);
        await ExecuteAsync(connection, "REVOKE CREATE ON DATABASE agentweaver FROM PUBLIC");

        Assert.False((await ReadProductionPreflightAsync(connection)).ForeignPublicAuthority);
        InitializerChecks.AssertPreflight(await ReadProductionPreflightAsync(connection));
        Assert.Equal(functionAcls, await ReadBrokerFunctionAclsAsync(connection));
    }

    private static async Task CreateBrokerFixtureAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, "CREATE SCHEMA identity_broker");
        await ExecuteAsync(connection,
            "CREATE TABLE identity_broker.secret_grant_operations (id integer PRIMARY KEY)");
        await ExecuteAsync(connection,
            "CREATE TABLE identity_broker.secret_grant_revisions (id integer PRIMARY KEY)");
        await ExecuteAsync(connection, """
            CREATE FUNCTION identity_broker.complete_secret_grant_operation_once() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER AS $$ BEGIN RETURN NEW; END $$
            """);
        await ExecuteAsync(connection, """
            CREATE FUNCTION identity_broker.reject_secret_grant_revision_mutation() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER AS $$ BEGIN RETURN OLD; END $$
            """);
        await ExecuteAsync(connection, """
            CREATE FUNCTION identity_broker.require_secret_grant_revision_advance() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER AS $$ BEGIN RETURN NEW; END $$
            """);
        await ExecuteAsync(connection, """
            CREATE TRIGGER complete_secret_grant_operation_once
            BEFORE INSERT ON identity_broker.secret_grant_operations
            FOR EACH ROW EXECUTE FUNCTION identity_broker.complete_secret_grant_operation_once()
            """);
        await ExecuteAsync(connection, """
            CREATE TRIGGER reject_secret_grant_revision_mutation
            BEFORE UPDATE ON identity_broker.secret_grant_revisions
            FOR EACH ROW EXECUTE FUNCTION identity_broker.reject_secret_grant_revision_mutation()
            """);
        await ExecuteAsync(connection, """
            CREATE TRIGGER require_secret_grant_revision_advance
            BEFORE INSERT ON identity_broker.secret_grant_revisions
            FOR EACH ROW EXECUTE FUNCTION identity_broker.require_secret_grant_revision_advance()
            """);
    }

    private static async Task AssertPreflightRejectedAsync(NpgsqlConnection connection,
        bool? foreignAuthority = null, bool? incompatibleDefaults = null)
    {
        var state = await ReadProductionPreflightAsync(connection);
        if (foreignAuthority.HasValue)
            Assert.Equal(foreignAuthority.Value, state.ForeignPublicAuthority);
        if (incompatibleDefaults.HasValue)
            Assert.Equal(incompatibleDefaults.Value, state.IncompatibleDefaults);
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertPreflight(state));
    }

    private static async Task<PreflightSnapshot> ReadProductionPreflightAsync(NpgsqlConnection connection)
    {
        var state = await ReadJsonAsync<PreflightSnapshot>(connection, InitializerChecks.PreflightQuery);
        return state with { Boundary = await ReadExistingBoundaryAsync(connection) };
    }

    private static async Task<ExistingBoundary> ReadExistingBoundaryAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(InitializerChecks.ExistingBoundaryQuery, connection);
        command.Parameters.AddWithValue("schemas", ServiceSchemas);
        var json = await command.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException("The initializer boundary query returned no result.");
        return JsonSerializer.Deserialize<ExistingBoundary>(json, JsonOptions)
            ?? throw new InvalidOperationException("The initializer boundary query returned no state.");
    }

    private static async Task<bool> RuntimeCanExecuteBrokerTriggerAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.has_function_privilege(
                'p1_initializer_runtime',
                'identity_broker.complete_secret_grant_operation_once()',
                'EXECUTE')
            """, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadBrokerFunctionAclsAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT coalesce(jsonb_agg(
                jsonb_build_array(p.proname,
                    coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))::text)
                ORDER BY p.proname)::text, '[]')
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'identity_broker'
              AND p.proname IN (
                'complete_secret_grant_operation_once',
                'reject_secret_grant_revision_mutation',
                'require_secret_grant_revision_advance')
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<T> ReadJsonAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var json = await command.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException("The initializer query returned no result.");
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException("The initializer query returned no state.");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
