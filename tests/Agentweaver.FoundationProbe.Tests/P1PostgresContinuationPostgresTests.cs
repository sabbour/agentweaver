using System.Diagnostics;
using System.Text.Json;
using Agentweaver.P1PostgresInitializer;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.FoundationProbe.Tests;

public sealed class P1PostgresContinuationPostgresTests(ProbePostgresFixture fixture)
    : IClassFixture<ProbePostgresFixture>
{
    [Fact]
    public async Task ContinuationMigratesAllServicesFromTheExactProjectsPrefixWithoutRuntimeGrants()
    {
        await using var state = await ContinuationState.CreateAsync(fixture);
        await state.CreateProjectsPrefixAsync();

        var completed = new List<ServicePlan>();
        var stages = new List<string>();

        async Task VerifyBeforeEachSchemaAsync()
        {
            await using var connection = await state.DataSource.OpenConnectionAsync();
            await state.AssertProjectsPrefixAsync(
                connection, requireOnlyProjectsSchema: completed.Count == 0, CancellationToken.None);
            foreach (var service in completed)
            {
                await P1PostgresContinuation.VerifyServiceMigrationsAsync(
                    state.DataSource, service, CancellationToken.None);
                await AssertSchemaOwnedAndRuntimeDeniedAsync(
                    connection, service, state);
            }
        }

        async Task VerifyOwnedObjectsAsync(ServicePlan service)
        {
            await using var connection = await state.DataSource.OpenConnectionAsync();
            await AssertSchemaOwnedAndRuntimeDeniedAsync(
                connection, service, state);
            completed.Add(service);
        }

        await P1PostgresContinuation.MigrateRemainingServicesAsync(
            state.DataSource,
            state.Plan,
            state.OperatorName,
            stage =>
            {
                stages.Add(stage);
                return Task.CompletedTask;
            },
            VerifyBeforeEachSchemaAsync,
            VerifyOwnedObjectsAsync,
            CancellationToken.None);

        Assert.Equal(state.Plan.Services.Skip(1).SelectMany(service =>
            new[] { $"{service.Service}:schema_created", $"{service.Service}:migrated", $"{service.Service}:exact_inventory_checked" }),
            stages);
        Assert.Equal(state.Plan.Services.Skip(1), completed);

        await using var finalConnection = await state.DataSource.OpenConnectionAsync();
        await state.AssertProjectsPrefixAsync(
            finalConnection, requireOnlyProjectsSchema: false, CancellationToken.None);
        foreach (var service in state.Plan.Services)
        {
            await P1PostgresContinuation.VerifyServiceMigrationsAsync(
                state.DataSource, service, CancellationToken.None);
            await AssertSchemaOwnedAndRuntimeDeniedAsync(
                finalConnection, service, state);
        }
        await AssertOwnerOnlyPrivilegesAbsentAsync(finalConnection, state);
    }

    [Fact]
    public async Task ContinuationRejectsAnOwnerOnlyTableGrantBeforeCreatingAnotherServiceSchema()
    {
        await using var state = await ContinuationState.CreateAsync(fixture);
        await state.CreateProjectsPrefixAsync();

        await using var connection = await state.DataSource.OpenConnectionAsync();
        await state.AssertProjectsPrefixAsync(
            connection, requireOnlyProjectsSchema: true, CancellationToken.None);
        await ExecuteAsync(connection,
            $"GRANT SELECT ON TABLE projects_config.project_setup_operations TO {InitializerChecks.Quote(state.RuntimeRoles[0])}");

        var stages = new List<string>();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            P1PostgresContinuation.MigrateRemainingServicesAsync(
                state.DataSource,
                state.Plan,
                state.OperatorName,
                stage =>
                {
                    stages.Add(stage);
                    return Task.CompletedTask;
                },
                () => state.AssertProjectsPrefixAsync(
                    connection, requireOnlyProjectsSchema: true, CancellationToken.None),
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Contains("ACL", failure.Message, StringComparison.Ordinal);
        Assert.Empty(stages);
        await using var verify = await state.DataSource.OpenConnectionAsync();
        Assert.True(await ScalarAsync<bool>(verify, """
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_namespace
                WHERE nspname = ANY(@schemas))
            """, ("schemas", state.Plan.Services.Skip(1).Select(service => service.Schema).ToArray())));
    }

    [Fact]
    public async Task ContinuationRejectsAnExplicitRuntimeFunctionGrantBeforeCreatingAnotherServiceSchema()
    {
        await using var state = await ContinuationState.CreateAsync(fixture);
        await state.CreateProjectsPrefixAsync();

        await using var connection = await state.DataSource.OpenConnectionAsync();
        await state.AssertProjectsPrefixAsync(
            connection, requireOnlyProjectsSchema: true, CancellationToken.None);
        await ExecuteAsync(connection, $"""
            GRANT EXECUTE ON FUNCTION projects_config.reject_project_setup_operation_mutation()
            TO {InitializerChecks.Quote(state.RuntimeRoles[0])}
            """);

        var stages = new List<string>();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            P1PostgresContinuation.MigrateRemainingServicesAsync(
                state.DataSource,
                state.Plan,
                state.OperatorName,
                stage =>
                {
                    stages.Add(stage);
                    return Task.CompletedTask;
                },
                () => state.AssertProjectsPrefixAsync(
                    connection, requireOnlyProjectsSchema: true, CancellationToken.None),
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Contains("EXECUTE", failure.Message, StringComparison.Ordinal);
        Assert.Empty(stages);
        await using var verify = await state.DataSource.OpenConnectionAsync();
        Assert.True(await ScalarAsync<bool>(verify, """
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_namespace
                WHERE nspname = ANY(@schemas))
            """, ("schemas", state.Plan.Services.Skip(1).Select(service => service.Schema).ToArray())));
    }

    [Fact]
    public async Task ContinuationRejectsRuntimeFunctionGrantOnMigratedServiceBeforeCreatingNextSchema()
    {
        await using var state = await ContinuationState.CreateAsync(fixture);
        await state.CreateProjectsPrefixAsync();

        var completed = new List<ServicePlan>();
        var stages = new List<string>();

        async Task VerifyBeforeEachSchemaAsync()
        {
            await using var connection = await state.DataSource.OpenConnectionAsync();
            await state.AssertProjectsPrefixAsync(
                connection, requireOnlyProjectsSchema: completed.Count == 0, CancellationToken.None);
            foreach (var service in completed)
            {
                await P1PostgresContinuation.VerifyServiceMigrationsAsync(
                    state.DataSource, service, CancellationToken.None);
                await AssertSchemaOwnedAndRuntimeDeniedAsync(connection, service, state);
            }
        }

        async Task VerifyOwnedObjectsAsync(ServicePlan service)
        {
            await using var connection = await state.DataSource.OpenConnectionAsync();
            await AssertSchemaOwnedAndRuntimeDeniedAsync(connection, service, state);
            if (service.Service == "knowledge")
            {
                var function = service.Functions.First();
                await ExecuteAsync(connection, $"""
                    GRANT EXECUTE ON FUNCTION {InitializerChecks.Quote(service.Schema)}.{function.Signature}
                    TO {InitializerChecks.Quote(service.Role)}
                    """);
            }
            completed.Add(service);
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            P1PostgresContinuation.MigrateRemainingServicesAsync(
                state.DataSource,
                state.Plan,
                state.OperatorName,
                stage =>
                {
                    stages.Add(stage);
                    return Task.CompletedTask;
                },
                VerifyBeforeEachSchemaAsync,
                VerifyOwnedObjectsAsync,
                CancellationToken.None));

        Assert.Contains("EXECUTE", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("events-and-sessions:schema_created", stages);
        await using var verify = await state.DataSource.OpenConnectionAsync();
        Assert.True(await ScalarAsync<bool>(verify, """
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_namespace
                WHERE nspname = ANY(@schemas))
            """, ("schemas", state.Plan.Services.Skip(3).Select(service => service.Schema).ToArray())));
    }

    [Fact]
    public async Task ContinuationRejectsGrantedViewsBeforeCreatingAnotherServiceSchema()
    {
        await using var state = await ContinuationState.CreateAsync(fixture);
        await state.CreateProjectsPrefixAsync();

        await using var connection = await state.DataSource.OpenConnectionAsync();
        await ExecuteAsync(connection, """
            CREATE VIEW projects_config.project_setup_operations_view AS
                SELECT * FROM projects_config.project_setup_operations;
            CREATE MATERIALIZED VIEW projects_config.project_setup_operations_snapshot AS
                SELECT * FROM projects_config.project_setup_operations;
            """);
        await ExecuteAsync(connection, $"""
            GRANT SELECT ON TABLE
                projects_config.project_setup_operations_view,
                projects_config.project_setup_operations_snapshot
            TO {InitializerChecks.Quote(state.RuntimeRoles[0])}
            """);

        var stages = new List<string>();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            P1PostgresContinuation.MigrateRemainingServicesAsync(
                state.DataSource,
                state.Plan,
                state.OperatorName,
                stage =>
                {
                    stages.Add(stage);
                    return Task.CompletedTask;
                },
                () => state.AssertProjectsPrefixAsync(
                    connection, requireOnlyProjectsSchema: true, CancellationToken.None),
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Contains("unexpected or foreign-owned objects", failure.Message, StringComparison.Ordinal);
        Assert.Empty(stages);
        await using var verify = await state.DataSource.OpenConnectionAsync();
        Assert.True(await ScalarAsync<bool>(verify, """
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_namespace
                WHERE nspname = ANY(@schemas))
            """, ("schemas", state.Plan.Services.Skip(1).Select(service => service.Schema).ToArray())));
    }

    private static async Task AssertSchemaOwnedAndRuntimeDeniedAsync(
        NpgsqlConnection connection,
        ServicePlan service,
        ContinuationState state)
    {
        await P1PostgresContinuation.AssertOwnedObjectsAsync(
            connection, service, state.OperatorName, CancellationToken.None);
        await using var command = new NpgsqlCommand("""
            SELECT namespace.nspowner = @owner_oid
              AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_class relation
                WHERE relation.relnamespace = namespace.oid
                  AND relation.relkind IN ('r', 'p', 'f', 'S', 'v', 'm')
                  AND relation.relowner <> namespace.nspowner)
              AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_proc function
                WHERE function.pronamespace = namespace.oid
                  AND function.proowner <> namespace.nspowner)
              AND NOT EXISTS (
                SELECT 1
                FROM unnest(@roles) AS runtime(role_name)
                CROSS JOIN unnest(ARRAY['USAGE', 'CREATE']) AS privilege(name)
                WHERE pg_catalog.has_schema_privilege(runtime.role_name, namespace.nspname, privilege.name))
              AND NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class relation
                CROSS JOIN unnest(@roles) AS runtime(role_name)
                CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilege(name)
                WHERE relation.relnamespace = namespace.oid
                  AND relation.relkind IN ('r', 'p', 'f', 'v', 'm')
                  AND pg_catalog.has_table_privilege(runtime.role_name, relation.oid, privilege.name))
              AND NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class sequence
                CROSS JOIN unnest(@roles) AS runtime(role_name)
                CROSS JOIN unnest(ARRAY['USAGE', 'SELECT', 'UPDATE']) AS privilege(name)
                WHERE sequence.relnamespace = namespace.oid
                  AND sequence.relkind = 'S'
                  AND pg_catalog.has_sequence_privilege(runtime.role_name, sequence.oid, privilege.name))
            FROM pg_catalog.pg_namespace namespace
            WHERE namespace.nspname = @schema
            """, connection);
        command.Parameters.AddWithValue("schema", service.Schema);
        command.Parameters.AddWithValue("owner_oid", NpgsqlDbType.Oid, state.OperatorOid);
        command.Parameters.AddWithValue("roles", state.RuntimeRoles);
        Assert.True(await command.ExecuteScalarAsync() is true,
            $"Objects in {service.Schema} must be operator-owned and have no runtime grants.");
        await P1PostgresContinuation.AssertServiceUnprivilegedAsync(
            connection, service, await state.ReadPrincipalsAsync(connection, CancellationToken.None),
            state.OperatorOid, CancellationToken.None);
    }

    private static async Task AssertOwnerOnlyPrivilegesAbsentAsync(
        NpgsqlConnection connection,
        ContinuationState state)
    {
        foreach (var role in state.RuntimeRoles)
        {
            Assert.False(await ScalarAsync<bool>(connection, """
                SELECT EXISTS (
                    SELECT 1
                    FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilege(name)
                    WHERE pg_catalog.has_table_privilege(@role, 'projects_config.project_setup_operations', privilege.name))
                """, ("role", role)));
            Assert.False(await ScalarAsync<bool>(connection, """
                SELECT EXISTS (
                    SELECT 1
                    FROM unnest(ARRAY['USAGE', 'SELECT', 'UPDATE']) AS privilege(name)
                    WHERE pg_catalog.has_sequence_privilege(
                        @role, 'projects_config.platform_runtime_revisions_revision_seq', privilege.name))
                """, ("role", role)));
        }
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ContinuationState(
        NpgsqlDataSource adminDataSource,
        NpgsqlDataSource dataSource,
        string databaseName,
        string metadataSchema,
        string operatorName,
        uint operatorOid,
        PostgresPlan plan) : IAsyncDisposable
    {
        private const string TenantId = "11111111-1111-1111-1111-111111111111";

        public NpgsqlDataSource DataSource { get; } = dataSource;
        public string OperatorName { get; } = operatorName;
        public uint OperatorOid { get; } = operatorOid;
        public PostgresPlan Plan { get; } = plan;
        public string[] RuntimeRoles { get; } = plan.Services.Select(service => service.Role).ToArray();

        public static async Task<ContinuationState> CreateAsync(ProbePostgresFixture fixture)
        {
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var databaseName = $"p1db_{suffix}";
            var operatorName = $"p1op_{suffix}";
            var metadataSchema = $"p1meta_{suffix}";
            var password = Guid.NewGuid().ToString("N");
            var plan = await CreatePlanAsync(fixture.ConnectionString, databaseName, operatorName, suffix);

            await using (var admin = await fixture.AdminDataSource.OpenConnectionAsync())
            {
                await ExecuteAsync(admin, $"""
                    CREATE ROLE {InitializerChecks.Quote(operatorName)}
                    LOGIN PASSWORD '{password}'
                    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT
                    """);
                await ExecuteAsync(admin,
                    $"CREATE DATABASE {InitializerChecks.Quote(databaseName)} OWNER {InitializerChecks.Quote(operatorName)}");
                foreach (var service in plan.Services)
                {
                    await ExecuteAsync(admin, $"""
                        CREATE ROLE {InitializerChecks.Quote(service.Role)}
                        LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT
                        """);
                    await ExecuteAsync(admin, $"""
                        GRANT {InitializerChecks.Quote(service.Role)} TO {InitializerChecks.Quote(operatorName)}
                        WITH ADMIN OPTION, INHERIT FALSE, SET FALSE
                        """);
                }
            }

            var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Database = databaseName,
                Username = operatorName,
                Password = password,
                Pooling = false,
            }.ConnectionString;
            var dataSource = NpgsqlDataSource.Create(connectionString);
            await using var connection = await dataSource.OpenConnectionAsync();
            var operatorOid = await ScalarAsync<uint>(
                connection, "SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user");
            return new ContinuationState(
                fixture.AdminDataSource, dataSource, databaseName, metadataSchema,
                operatorName, operatorOid, plan);
        }

        public async Task CreateProjectsPrefixAsync()
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await ExecuteAsync(connection,
                $"CREATE SCHEMA projects_config AUTHORIZATION {InitializerChecks.Quote(OperatorName)}");
            await ExecuteAsync(connection, "REVOKE ALL ON SCHEMA projects_config FROM PUBLIC");
            await ExecuteAsync(connection, BuildPrincipalFunctionSql());
            await P1PostgresContinuation.MigrateServiceAsync(
                DataSource, Plan.Services[0], CancellationToken.None);
            await P1PostgresContinuation.VerifyServiceMigrationsAsync(
                DataSource, Plan.Services[0], CancellationToken.None);
        }

        public async Task AssertProjectsPrefixAsync(
            NpgsqlConnection connection,
            bool requireOnlyProjectsSchema,
            CancellationToken cancellationToken)
        {
            var principals = await ReadPrincipalsAsync(connection, cancellationToken);
            await P1PostgresContinuation.AssertProjectsPrefixAsync(
                DataSource, connection, Plan, principals, OperatorOid, OperatorName,
                TenantId, cancellationToken, requireOnlyProjectsSchema);
        }

        public Task<RuntimePrincipal[]> ReadPrincipalsAsync(
            NpgsqlConnection connection,
            CancellationToken cancellationToken) =>
            P1PostgresContinuation.ReadPrincipalsAsync(
                connection, null, Plan, cancellationToken,
                $"{InitializerChecks.Quote(metadataSchema)}.principals");

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await using var admin = await adminDataSource.OpenConnectionAsync();
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {InitializerChecks.Quote(databaseName)}");
            foreach (var role in RuntimeRoles)
                await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {InitializerChecks.Quote(role)}");
            await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {InitializerChecks.Quote(OperatorName)}");
        }

        private string BuildPrincipalFunctionSql()
        {
            var rows = string.Join(",\n", Plan.Services.Select(service =>
                $"('{service.Role}', '{service.PrincipalObjectId}')"));
            return $"""
                CREATE SCHEMA {InitializerChecks.Quote(metadataSchema)} AUTHORIZATION {InitializerChecks.Quote(OperatorName)};
                CREATE FUNCTION {InitializerChecks.Quote(metadataSchema)}.principals(boolean)
                RETURNS TABLE(role_name name, principal_type text, object_id text, tenant_id text, is_mfa integer, is_admin integer)
                LANGUAGE sql STABLE AS $function$
                    SELECT mapping.role_name::name, 'service'::text, mapping.object_id,
                           '{TenantId}'::text, 0, 0
                    FROM (VALUES {rows}) AS mapping(role_name, object_id)
                $function$
                """;
        }

        private static async Task<PostgresPlan> CreatePlanAsync(
            string connectionString,
            string databaseName,
            string operatorName,
            string suffix)
        {
            var repositoryRoot = FindRepositoryRoot();
            var startInfo = new ProcessStartInfo("node")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(Path.Combine("scripts", "azure", "lib", "p1-postgres-plan.mjs"));
            startInfo.ArgumentList.Add("--json");
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("The P1 PostgreSQL source plan process did not start.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The P1 PostgreSQL source plan failed: {error}");
            var sourcePlan = JsonSerializer.Deserialize<PostgresPlan>(
                output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("The P1 PostgreSQL source plan was empty.");
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var target = sourcePlan.Target with
            {
                PostgresHost = builder.Host!,
                Database = databaseName,
                OperatorName = operatorName,
                OperatorObjectId = Guid.NewGuid().ToString("D"),
                ConnectionHost = builder.Host!,
                ConnectionPort = builder.Port,
            };
            var services = sourcePlan.Services.Select(service => service with
            {
                Role = $"p1rt_{suffix}_{service.Service.Replace("-", "_", StringComparison.Ordinal)}",
                PrincipalObjectId = Guid.NewGuid().ToString("D"),
            }).ToArray();
            return sourcePlan with { Target = target, Services = services };
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "scripts", "azure", "lib", "p1-postgres-plan.mjs")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("The P1 PostgreSQL source plan was not found above the test output directory.");
        }
    }
}
