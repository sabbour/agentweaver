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

    [Fact]
    public async Task ProjectsConfigGrantChecksSeeNullSchemaAclsAndColumnGrants()
    {
        await using (var maintenance = new NpgsqlConnection(fixture.ConnectionString))
        {
            await maintenance.OpenAsync();
            await ExecuteAsync(maintenance, "CREATE DATABASE p1_projects_grants");
        }
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = "p1_projects_grants",
        }.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE SCHEMA projects_config");
        await ExecuteAsync(connection, "CREATE ROLE p1_projects_runtime NOLOGIN");
        var ownerOid = await ScalarOidAsync(connection,
            "SELECT nspowner FROM pg_catalog.pg_namespace WHERE nspname = 'projects_config'");
        var runtimeOid = await ScalarOidAsync(connection,
            "SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'p1_projects_runtime'");
        var service = new ServicePlan("projects-config", "projects_config", "p1_projects_runtime", "object-id",
        [
            new TablePlan("requests", ["SELECT"], null, false, null),
            new TablePlan("locks", ["SELECT"], "lock_owner", false, null),
        ], []);

        // A fresh schema has a NULL ACL. The query must expand it to the owner's defaults, so the grant
        // step accepts the state it exists to repair.
        Assert.Null(await ScalarAsync(connection,
            "SELECT nspacl::text FROM pg_catalog.pg_namespace WHERE nspname = 'projects_config'"));
        InitializerChecks.AssertServiceSchemaAcl(service, await ReadSchemaAclAsync(connection), ownerOid, runtimeOid,
            allowMissingRuntimeUsage: true);

        await ExecuteAsync(connection, "CREATE TABLE projects_config.requests (id int, status text)");
        await ExecuteAsync(connection, "CREATE TABLE projects_config.locks (id int, lock_owner text)");
        await ExecuteAsync(connection,
            "GRANT SELECT ON projects_config.requests, projects_config.locks TO p1_projects_runtime");
        await ExecuteAsync(connection, "GRANT UPDATE (lock_owner) ON projects_config.locks TO p1_projects_runtime");
        InitializerChecks.AssertRuntimeColumnGrants(service, await ReadColumnGrantsAsync(connection, runtimeOid));

        // A single-column UPDATE on a read-only table is invisible to table-level privilege checks.
        await ExecuteAsync(connection, "GRANT UPDATE (status) ON projects_config.requests TO p1_projects_runtime");
        var hidden = await ReadColumnGrantsAsync(connection, runtimeOid);
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertRuntimeColumnGrants(service, hidden));
        await ExecuteAsync(connection, "REVOKE UPDATE (status) ON projects_config.requests FROM p1_projects_runtime");

        // A PUBLIC column grant reaches the runtime role too.
        await ExecuteAsync(connection, "GRANT UPDATE (lock_owner) ON projects_config.locks TO PUBLIC");
        var publicGrant = await ReadColumnGrantsAsync(connection, runtimeOid);
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertRuntimeColumnGrants(service, publicGrant));
    }

    private static async Task<uint> ScalarOidAsync(NpgsqlConnection connection, string sql) =>
        Convert.ToUInt32(await ScalarAsync(connection, sql));

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task<SchemaAclState?> ReadSchemaAclAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(InitializerChecks.SchemaAclQuery, connection);
        command.Parameters.AddWithValue("schema", "projects_config");
        return JsonSerializer.Deserialize<SchemaAclState>((string)(await command.ExecuteScalarAsync())!, JsonOptions);
    }

    private static async Task<List<ColumnGrant>> ReadColumnGrantsAsync(NpgsqlConnection connection, uint runtimeOid)
    {
        await using var command = new NpgsqlCommand(InitializerChecks.RuntimeColumnGrantsQuery, connection);
        command.Parameters.AddWithValue("schema", "projects_config");
        command.Parameters.AddWithValue("runtime", NpgsqlTypes.NpgsqlDbType.Oid, runtimeOid);
        var grants = new List<ColumnGrant>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            grants.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        return grants;
    }

    [Fact]
    public async Task ProductionRoleSetupSqlWorksForNonSuperuserCreateRoleOperatorAndCommitsExactState()
    {
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await PrepareRoleSetupFixtureAsync(admin);
        try
        {
            await using var operatorConnection = await OpenOperatorAsync();
            Assert.False(await ScalarAsync<bool>(operatorConnection,
                "SELECT rolsuper OR rolcreatedb OR rolreplication OR rolbypassrls FROM pg_roles WHERE rolname = current_user"));
            await using var transaction = await operatorConnection.BeginTransactionAsync();
            await CreateRuntimeRolesAsync(operatorConnection, transaction);
            var operatorOid = await ScalarAsync<uint>(operatorConnection,
                "SELECT oid FROM pg_roles WHERE rolname = current_user", transaction);
            var initialPrincipals = await ReadRoleSetupPrincipalsAsync(operatorConnection, transaction);
            var expectedGrantorOids = InitializerChecks.CaptureMembershipGrantors(
                RoleSetupServices, initialPrincipals, operatorOid, RoleSetupTenant);
            Assert.All(expectedGrantorOids.Values, grantorOid => Assert.NotEqual(operatorOid, grantorOid));

            var stages = new List<string>();
            var committed = false;
            await InitializerChecks.RunStageAsync(() => Task.CompletedTask,
                async () =>
                {
                    foreach (var service in RoleSetupServices)
                        foreach (var (stage, sql) in InitializerChecks.RolePostCreateStatements(
                            service.Role, RoleSetupOperator, expectedGrantorOids[service.Role], operatorOid))
                        {
                            stages.Add(stage);
                            await ExecuteAsync(operatorConnection, sql, transaction);
                        }
                },
                async () =>
                {
                    var principals = await ReadRoleSetupPrincipalsAsync(operatorConnection, transaction);
                    InitializerChecks.AssertPrincipals(
                        RoleSetupServices, principals, operatorOid, RoleSetupTenant, expectedGrantorOids);
                },
                async () => { await transaction.CommitAsync(); committed = true; },
                () => Task.CompletedTask, observed =>
                {
                    stages.Add(observed);
                    return Task.CompletedTask;
                });

            Assert.True(committed);
            Assert.Contains(InitializerChecks.RoleMembershipCleanupStage, stages);
            var expectedStages = RoleSetupServices.SelectMany(service =>
                InitializerChecks.RolePostCreateStatements(service.Role, RoleSetupOperator,
                    expectedGrantorOids[service.Role], operatorOid).Select(item => item.Stage))
                .Concat(["role_readback", "role_commit"]);
            Assert.Equal(expectedStages, stages);
        }
        finally
        {
            await DropRoleSetupFixtureAsync(admin);
        }
    }

    [Theory]
    [InlineData("CREATEDB")]
    [InlineData("CREATEROLE")]
    [InlineData("REPLICATION")]
    [InlineData("BYPASSRLS")]
    [InlineData("SUPERUSER")]
    public async Task UnsafeFlagsAreNeverMaskedAndAreRejectedBeforeCommit(string flag)
    {
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await PrepareRoleSetupFixtureAsync(admin);
        try
        {
            await using var operatorConnection = await OpenOperatorAsync();
            await CreateRuntimeRolesAsync(operatorConnection);
            var operatorOid = await ScalarAsync<uint>(operatorConnection,
                "SELECT oid FROM pg_roles WHERE rolname = current_user");
            var initialPrincipals = await ReadRoleSetupPrincipalsAsync(operatorConnection, null);
            var expectedGrantorOids = InitializerChecks.CaptureMembershipGrantors(
                RoleSetupServices, initialPrincipals, operatorOid, RoleSetupTenant);
            await ExecuteAsync(admin,
                $"ALTER ROLE {InitializerChecks.Quote(RoleSetupServices[0].Role)} {flag}");

            await using var transaction = await operatorConnection.BeginTransactionAsync();
            var committed = false;
            var observed = new List<string>();
            async Task RunPostCreateAsync() =>
                await InitializerChecks.RunStageAsync(
                    () => Task.CompletedTask,
                    async () =>
                    {
                        foreach (var service in RoleSetupServices)
                            foreach (var (stage, sql) in InitializerChecks.RolePostCreateStatements(
                                service.Role, RoleSetupOperator, expectedGrantorOids[service.Role], operatorOid))
                            {
                                observed.Add(stage);
                                await ExecuteAsync(operatorConnection, sql, transaction);
                            }
                    },
                    async () => InitializerChecks.AssertPrincipals(RoleSetupServices,
                        await ReadRoleSetupPrincipalsAsync(operatorConnection, transaction), operatorOid,
                        RoleSetupTenant, expectedGrantorOids),
                    async () => { await transaction.CommitAsync(); committed = true; },
                    () => Task.CompletedTask, stage =>
                    {
                        observed.Add(stage);
                        return Task.CompletedTask;
                    });

            if (flag == "SUPERUSER")
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(RunPostCreateAsync);
                Assert.Equal("42501", failure.SqlState);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(RunPostCreateAsync);
                Assert.Equal("role_readback", observed[^1]);
            }
            await transaction.RollbackAsync();

            Assert.False(committed);
        }
        finally
        {
            await DropRoleSetupFixtureAsync(admin);
        }
    }

    private const string RoleSetupOperator = "p1_role_setup_operator";
    private const string RoleSetupTenant = "11111111-1111-1111-1111-111111111111";

    private static readonly ServicePlan[] RoleSetupServices = Enumerable.Range(1, 5).Select(index =>
        new ServicePlan($"service-{index}", $"schema_{index}", $"p1_rt_{index}",
            $"00000000-0000-0000-0000-{index:000000000000}", [], [])).ToArray();

    private static async Task CreateRuntimeRolesAsync(NpgsqlConnection operatorConnection,
        NpgsqlTransaction? transaction = null)
    {
        foreach (var service in RoleSetupServices)
            await ExecuteAsync(operatorConnection,
                $"CREATE ROLE {InitializerChecks.Quote(service.Role)} LOGIN", transaction);
    }

    // Read the real grantor OID from the catalog; never substitute the operator OID.
    private static async Task<RuntimePrincipal[]> ReadRoleSetupPrincipalsAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction)
    {
        await using var command = new NpgsqlCommand(
            InitializerChecks.PrincipalReadbackQuery("public.p1_role_setup_principals"), connection, transaction);
        command.Parameters.AddWithValue("roles", RoleSetupServices.Select(item => item.Role).ToArray());
        var json = (string)(await command.ExecuteScalarAsync())!;
        return JsonSerializer.Deserialize<RuntimePrincipal[]>(json, JsonOptions)!;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task PrepareRoleSetupFixtureAsync(NpgsqlConnection admin)
    {
        await DropRoleSetupFixtureAsync(admin);
        await ExecuteAsync(admin,
            $"CREATE ROLE {RoleSetupOperator} LOGIN PASSWORD 'operator-secret' NOSUPERUSER NOCREATEDB CREATEROLE");
        await ExecuteAsync(admin, $$"""
            CREATE OR REPLACE FUNCTION public.p1_role_setup_principals(boolean)
            RETURNS TABLE(role_name name, principal_type text, object_id text, tenant_id text, is_mfa integer, is_admin integer)
            LANGUAGE sql STABLE AS $$
              SELECT rolname, 'service', '00000000-0000-0000-0000-' || lpad(right(rolname, 1), 12, '0'),
                     '{{RoleSetupTenant}}', 0, 0
              FROM pg_catalog.pg_roles WHERE rolname ~ '^p1_rt_[0-9]$'
            $$
            """);
    }

    private static async Task DropRoleSetupFixtureAsync(NpgsqlConnection admin)
    {
        foreach (var service in RoleSetupServices)
            await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {InitializerChecks.Quote(service.Role)}");
        await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {RoleSetupOperator}");
    }

    private async Task<NpgsqlConnection> OpenOperatorAsync()
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Username = RoleSetupOperator,
            Password = "operator-secret",
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync();
        return connection;
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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
