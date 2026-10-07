using Agentweaver.Identity.Broker;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class IdentityBrokerPostgresBootstrapCommandTests(PostgresContainerFixture postgres)
{
    [Fact]
    public void BootstrapAndVerificationCommandsRequireOneExplicitArgument()
    {
        Assert.True(IdentityBrokerPostgresBootstrapCommand.IsRequested(["--bootstrap-identity-postgres"]));
        Assert.False(IdentityBrokerPostgresBootstrapCommand.IsRequested(["--bootstrap-identity-postgres", "extra"]));
        Assert.True(IdentityBrokerPostgresBootstrapCommand.IsVerifyRequested(["--verify-identity-postgres-bootstrap"]));
        Assert.False(IdentityBrokerPostgresBootstrapCommand.IsVerifyRequested(["--verify-identity-postgres-bootstrap", "extra"]));
        Assert.True(IdentityBrokerPostgresBootstrapCommand.ContainsArgument(["--bootstrap-identity-postgres"]));
        Assert.True(IdentityBrokerPostgresBootstrapCommand.ContainsArgument(["--verify-identity-postgres-bootstrap"]));
        Assert.False(IdentityBrokerPostgresBootstrapCommand.ContainsArgument(["--execute"]));
    }

    [Fact]
    public void ReadsPasswordFreeVerifyFullConnectionThroughOnlyTheInstallerLoopbackPort()
    {
        var runtimePrincipal = Guid.NewGuid();
        var migrationPrincipal = Guid.NewGuid();
        var configuration = Configuration(
            "Host=127.0.0.1;Port=15432;Database=postgres;Username=admin@example.test;SSL Mode=VerifyFull",
            runtimePrincipal,
            migrationPrincipal);

        var options = IdentityBrokerPostgresBootstrapCommand.ReadOptions(configuration);

        Assert.Equal("aw-v1-p0-pg.postgres.database.azure.com", options.PostgresHost);
        Assert.Equal("agentweaver", options.DatabaseName);
        Assert.Equal(runtimePrincipal.ToString(), options.RuntimePrincipalObjectId);
        Assert.Equal(migrationPrincipal.ToString(), options.MigrationPrincipalObjectId);
        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        Assert.Equal("127.0.0.1", builder.Host);
        Assert.Equal(15432, builder.Port);
        Assert.Equal("admin@example.test", builder.Username);
        Assert.True(string.IsNullOrEmpty(builder.Password));
    }

    [Fact]
    public void ReadsAdminUsernameFromItsSeparateConfigurationValue()
    {
        var options = IdentityBrokerPostgresBootstrapCommand.ReadOptions(Configuration(
            "Host=127.0.0.1;Port=15432;Database=postgres;SSL Mode=VerifyFull"));

        Assert.Equal("admin@example.test", new NpgsqlConnectionStringBuilder(options.ConnectionString).Username);
    }

    [Fact]
    public void RejectsPasswordWrongTlsModeWrongTunnelAndReusedPrincipal()
    {
        var passwordConnection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 15432,
            Database = "postgres",
            Username = "admin@example.test",
            Password = "test-password",
            SslMode = SslMode.VerifyFull,
        };
        var invalidConnections = new[]
        {
            Configuration(passwordConnection.ConnectionString),
            Configuration("Host=127.0.0.1;Port=15432;Database=postgres;SSL Mode=Require"),
            Configuration("Host=127.0.0.1;Port=15433;Database=postgres;SSL Mode=VerifyFull"),
            Configuration("Host=aw-v1-p0-pg.postgres.database.azure.com;Port=5432;Database=postgres;SSL Mode=VerifyFull"),
            Configuration("Host=localhost;Port=15432;Database=postgres;SSL Mode=VerifyFull"),
            Configuration("Host=127.0.0.1;Port=15432;Database=postgres;SSL Mode=VerifyFull",
                Guid.Empty, Guid.Empty),
        };
        foreach (var configuration in invalidConnections)
            Assert.Throws<InvalidOperationException>(() =>
                IdentityBrokerPostgresBootstrapCommand.ReadOptions(configuration));
    }

    [Fact]
    public void UsesPgaadauthColumnsAndMembershipOptions()
    {
        Assert.Contains("rolname::text", IdentityBrokerPostgresBootstrapCommand.PrincipalCheckSql, StringComparison.Ordinal);
        Assert.Contains("principaltype = 'service'", IdentityBrokerPostgresBootstrapCommand.PrincipalCheckSql, StringComparison.Ordinal);
        Assert.Contains("isadmin = 0", IdentityBrokerPostgresBootstrapCommand.PrincipalCheckSql, StringComparison.Ordinal);
        Assert.DoesNotContain("is_admin", IdentityBrokerPostgresBootstrapCommand.PrincipalCheckSql, StringComparison.Ordinal);
        Assert.Contains("membership.admin_option", IdentityBrokerPostgresBootstrapCommand.OperatorMembershipCheckSql, StringComparison.Ordinal);
        Assert.Contains("membership.inherit_option", IdentityBrokerPostgresBootstrapCommand.OperatorMembershipCheckSql, StringComparison.Ordinal);
        Assert.Contains("membership.set_option", IdentityBrokerPostgresBootstrapCommand.OperatorMembershipCheckSql, StringComparison.Ordinal);
        Assert.Contains("pg_has_role", IdentityBrokerPostgresBootstrapCommand.SetRoleCheckSql, StringComparison.Ordinal);
        Assert.Contains("'SET'", IdentityBrokerPostgresBootstrapCommand.SetRoleCheckSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutesPrincipalContractAndRejectsWorkloadSetRoleMembership()
    {
        var databaseConnectionString = await postgres.CreateDatabaseAsync();
        var databaseBuilder = new NpgsqlConnectionStringBuilder(databaseConnectionString);
        var database = databaseBuilder.Database!;
        var runtimeRole = "identity_runtime_" + Guid.NewGuid().ToString("N");
        var migrationRole = "identity_migration_" + Guid.NewGuid().ToString("N");
        var adminRole = "identity_admin_" + Guid.NewGuid().ToString("N");
        var runtimePrincipal = Guid.NewGuid().ToString();
        var migrationPrincipal = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid().ToString();

        var adminBuilder = new NpgsqlConnectionStringBuilder(databaseConnectionString) { Database = "postgres" };
        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();
        var setup = $"""
            CREATE ROLE {QuoteIdentifier(adminRole)} LOGIN;
            CREATE ROLE azure_pg_admin NOLOGIN;
            CREATE ROLE {QuoteIdentifier(runtimeRole)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;
            CREATE ROLE {QuoteIdentifier(migrationRole)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;
            GRANT {QuoteIdentifier(runtimeRole)} TO {QuoteIdentifier(adminRole)} WITH ADMIN TRUE, INHERIT FALSE, SET FALSE;
            GRANT {QuoteIdentifier(migrationRole)} TO {QuoteIdentifier(adminRole)} WITH ADMIN TRUE, INHERIT FALSE, SET FALSE;
            CREATE FUNCTION pg_catalog.pgaadauth_list_principals(isadminvalue boolean)
            RETURNS TABLE(rolname name, principalType text, objectId text, tenantId text, isMfa integer, isAdmin integer)
            LANGUAGE SQL
            AS $function$
                SELECT '{runtimeRole}'::name, 'service'::text, '{runtimePrincipal}'::text, '{tenantId}'::text, 0, 0
                WHERE NOT isadminvalue
                UNION ALL
                SELECT '{migrationRole}'::name, 'service'::text, '{migrationPrincipal}'::text, '{tenantId}'::text, 0, 0
                WHERE NOT isadminvalue
            $function$;
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            REVOKE CONNECT, TEMPORARY ON DATABASE {QuoteIdentifier(database)} FROM PUBLIC;
            GRANT CONNECT ON DATABASE {QuoteIdentifier(database)} TO {QuoteIdentifier(runtimeRole)};
            GRANT CONNECT ON DATABASE {QuoteIdentifier(database)} TO {QuoteIdentifier(migrationRole)};
            """;
        await using (var setupCommand = new NpgsqlCommand(setup, adminConnection))
            await setupCommand.ExecuteNonQueryAsync();

        await using (var databaseConnection = new NpgsqlConnection(databaseConnectionString))
        {
            await databaseConnection.OpenAsync();
            var schema = $"CREATE SCHEMA identity_broker AUTHORIZATION {QuoteIdentifier(migrationRole)}; " +
                         $"REVOKE ALL ON SCHEMA identity_broker FROM PUBLIC; " +
                         $"GRANT USAGE ON SCHEMA identity_broker TO {QuoteIdentifier(runtimeRole)};";
            await using var schemaCommand = new NpgsqlCommand(schema, databaseConnection);
            await schemaCommand.ExecuteNonQueryAsync();
        }

        var options = new IdentityBrokerPostgresBootstrapOptions(
            adminBuilder.ConnectionString,
            "localhost",
            adminRole,
            database,
            runtimeRole,
            runtimePrincipal,
            migrationRole,
            migrationPrincipal);
        await IdentityBrokerPostgresBootstrapCommand.VerifyStateAsync(
            adminConnection, adminBuilder.ConnectionString, options, CancellationToken.None);

        await using (var addSetRole = new NpgsqlCommand(
            $"GRANT {QuoteIdentifier(migrationRole)} TO {QuoteIdentifier(runtimeRole)} WITH ADMIN FALSE, INHERIT FALSE, SET TRUE",
            adminConnection))
            await addSetRole.ExecuteNonQueryAsync();

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IdentityBrokerPostgresBootstrapCommand.VerifyStateAsync(
                adminConnection, adminBuilder.ConnectionString, options, CancellationToken.None));
        Assert.Contains("can SET ROLE", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppliesCanonicalRuntimeGrantsOnlyAfterMigrationAndPreservesHistoryReadOnly()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var runtime = "runtime_" + suffix;
        var migration = "migration_" + suffix;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var roles = new NpgsqlCommand($"""
            CREATE ROLE {QuoteIdentifier(runtime)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;
            CREATE ROLE {QuoteIdentifier(migration)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;
            CREATE SCHEMA identity_broker AUTHORIZATION {QuoteIdentifier(migration)};
            """, connection))
            await roles.ExecuteNonQueryAsync();
        Assert.False(await IdentityBrokerPostgresBootstrapCommand.ApplyRuntimeGrantsIfMigratedAsync(
            connection, runtime, migration));
        var tables = new[] {
            "broker_users", "pending_authorizations", "secret_grant_heads", "secret_grant_revisions",
            "secret_grant_operations", "OpenIddictApplications", "OpenIddictAuthorizations",
            "OpenIddictScopes", "OpenIddictTokens", "__ef_migrations_history",
            "runtime_grant_heads", "runtime_grant_revisions", "runtime_grant_operations",
            "runtime_grant_operation_receipts",
        };
        foreach (var table in tables)
        {
            await using var create = new NpgsqlCommand($"""
                CREATE TABLE identity_broker.{QuoteIdentifier(table)} (id integer);
                ALTER TABLE identity_broker.{QuoteIdentifier(table)} OWNER TO {QuoteIdentifier(migration)};
                """, connection);
            await create.ExecuteNonQueryAsync();
        }
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.True(await IdentityBrokerPostgresBootstrapCommand.ApplyRuntimeGrantsIfMigratedAsync(
                connection, runtime, migration));
        await using (var useRuntime = new NpgsqlCommand($"SET ROLE {QuoteIdentifier(runtime)}", connection))
            await useRuntime.ExecuteNonQueryAsync();
        await using (var permitted = new NpgsqlCommand("""
            SELECT * FROM identity_broker.__ef_migrations_history;
            INSERT INTO identity_broker."OpenIddictScopes" VALUES (1);
            UPDATE identity_broker."OpenIddictScopes" SET id = 2;
            DELETE FROM identity_broker."OpenIddictScopes";
            INSERT INTO identity_broker.runtime_grant_heads VALUES (1);
            UPDATE identity_broker.runtime_grant_heads SET id = 2;
            INSERT INTO identity_broker.runtime_grant_revisions VALUES (1);
            INSERT INTO identity_broker.runtime_grant_operations VALUES (1);
            INSERT INTO identity_broker.runtime_grant_operation_receipts VALUES (1);
            """, connection))
            await permitted.ExecuteNonQueryAsync();
        await using (var forbidden = new NpgsqlCommand(
            "INSERT INTO identity_broker.__ef_migrations_history VALUES (1)", connection))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        foreach (var table in tables.Where(table => table.StartsWith("runtime_grant_", StringComparison.Ordinal)))
        {
            foreach (var statement in new[]
            {
                $"DELETE FROM identity_broker.{QuoteIdentifier(table)}",
                $"TRUNCATE identity_broker.{QuoteIdentifier(table)}",
            }.Concat(table == "runtime_grant_heads" ? [] :
                new[] { $"UPDATE identity_broker.{QuoteIdentifier(table)} SET id = 2" }))
            {
                await using var forbidden = new NpgsqlCommand(statement, connection);
                var error = await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
        }
        await using (var reset = new NpgsqlCommand("RESET ROLE", connection))
            await reset.ExecuteNonQueryAsync();
        await using (var foreignOwner = new NpgsqlCommand(
            $"ALTER TABLE identity_broker.broker_users OWNER TO {QuoteIdentifier(runtime)}", connection))
            await foreignOwner.ExecuteNonQueryAsync();
        var ownershipError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IdentityBrokerPostgresBootstrapCommand.ApplyRuntimeGrantsIfMigratedAsync(connection, runtime, migration));
        Assert.Contains("different owner", ownershipError.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(
        string connectionString,
        Guid? runtimePrincipal = null,
        Guid? migrationPrincipal = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:IdentityBrokerBootstrap"] = connectionString,
            ["IdentityBroker:Bootstrap:PostgresHost"] = "aw-v1-p0-pg.postgres.database.azure.com",
            ["IdentityBroker:Bootstrap:AdminUsername"] = "admin@example.test",
            ["IdentityBroker:Bootstrap:DatabaseName"] = "agentweaver",
            ["IdentityBroker:Bootstrap:RuntimeRole"] = "aw-v1-p0-id-identity-broker",
            ["IdentityBroker:Bootstrap:RuntimePrincipalObjectId"] = (runtimePrincipal ?? Guid.NewGuid()).ToString(),
            ["IdentityBroker:Bootstrap:MigrationRole"] = "aw-v1-p0-id-identity-broker-migration",
            ["IdentityBroker:Bootstrap:MigrationPrincipalObjectId"] = (migrationPrincipal ?? Guid.NewGuid()).ToString(),
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
