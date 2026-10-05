extern alias AzureIdentity;

using Azure.Core;
using Npgsql;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;
using AzureCliCredential = AzureIdentity::Azure.Identity.AzureCliCredential;

namespace Agentweaver.Identity.Broker;

internal sealed record IdentityBrokerPostgresBootstrapOptions(
    string ConnectionString,
    string PostgresHost,
    string AdminUsername,
    string DatabaseName,
    string RuntimeRole,
    string RuntimePrincipalObjectId,
    string MigrationRole,
    string MigrationPrincipalObjectId);

internal static class IdentityBrokerPostgresBootstrapCommand
{
    private const string Argument = "--bootstrap-identity-postgres";
    private const string VerifyArgument = "--verify-identity-postgres-bootstrap";
    internal const string PrincipalCheckSql = """
        SELECT count(*) = 2
        FROM pg_catalog.pgaadauth_list_principals(false)
        WHERE principaltype = 'service'
          AND isadmin = 0
          AND ((rolname::text = @runtimeRole AND objectid = @runtimePrincipal)
            OR (rolname::text = @migrationRole AND objectid = @migrationPrincipal))
        """;
    internal const string OperatorMembershipCheckSql = """
        SELECT count(*) = 2
        FROM pg_catalog.pg_auth_members membership
        JOIN pg_catalog.pg_roles member_role ON member_role.oid = membership.member
        JOIN pg_catalog.pg_roles granted_role ON granted_role.oid = membership.roleid
        WHERE member_role.rolname = @adminUsername
          AND granted_role.rolname = ANY(@roles)
          AND membership.admin_option
          AND NOT membership.inherit_option
          AND NOT membership.set_option
        """;
    internal const string SetRoleCheckSql = """
        SELECT NOT pg_catalog.pg_has_role(@runtimeRole, @migrationRole, 'SET')
          AND NOT pg_catalog.pg_has_role(@migrationRole, @runtimeRole, 'SET')
          AND NOT pg_catalog.pg_has_role(@runtimeRole, @adminUsername, 'SET')
          AND NOT pg_catalog.pg_has_role(@migrationRole, @adminUsername, 'SET')
          AND NOT pg_catalog.pg_has_role(@runtimeRole, 'azure_pg_admin', 'SET')
          AND NOT pg_catalog.pg_has_role(@migrationRole, 'azure_pg_admin', 'SET')
        """;
    private static readonly HashSet<string> SystemDatabases = new(
        ["azure_maintenance", "azure_sys", "postgres"], StringComparer.Ordinal);

    public static bool IsRequested(string[] args) => args.Length == 1 && args[0] == Argument;

    public static bool IsVerifyRequested(string[] args) => args.Length == 1 && args[0] == VerifyArgument;

    public static bool ContainsArgument(string[] args) =>
        args.Contains(Argument, StringComparer.Ordinal) || args.Contains(VerifyArgument, StringComparer.Ordinal);

    public static IdentityBrokerPostgresBootstrapOptions ReadOptions(IConfiguration configuration)
    {
        var connectionString = Required(configuration.GetConnectionString("IdentityBrokerBootstrap"),
            "ConnectionStrings:IdentityBrokerBootstrap");
        var postgresHost = Required(configuration["IdentityBroker:Bootstrap:PostgresHost"],
            "IdentityBroker:Bootstrap:PostgresHost");
        var adminUsername = Required(configuration["IdentityBroker:Bootstrap:AdminUsername"],
            "IdentityBroker:Bootstrap:AdminUsername");
        var databaseName = Required(configuration["IdentityBroker:Bootstrap:DatabaseName"],
            "IdentityBroker:Bootstrap:DatabaseName");
        var runtimeRole = Required(configuration["IdentityBroker:Bootstrap:RuntimeRole"],
            "IdentityBroker:Bootstrap:RuntimeRole");
        var runtimePrincipal = Required(configuration["IdentityBroker:Bootstrap:RuntimePrincipalObjectId"],
            "IdentityBroker:Bootstrap:RuntimePrincipalObjectId");
        var migrationRole = Required(configuration["IdentityBroker:Bootstrap:MigrationRole"],
            "IdentityBroker:Bootstrap:MigrationRole");
        var migrationPrincipal = Required(configuration["IdentityBroker:Bootstrap:MigrationPrincipalObjectId"],
            "IdentityBroker:Bootstrap:MigrationPrincipalObjectId");

        if (Uri.CheckHostName(postgresHost) != UriHostNameType.Dns ||
            postgresHost.Contains('/', StringComparison.Ordinal))
            throw new InvalidOperationException("Identity broker bootstrap PostgresHost must be a DNS name.");
        if (!Guid.TryParse(runtimePrincipal, out _) || !Guid.TryParse(migrationPrincipal, out _) ||
            StringComparer.OrdinalIgnoreCase.Equals(runtimePrincipal, migrationPrincipal))
            throw new InvalidOperationException("Identity broker bootstrap requires two distinct UAMI principal object IDs.");
        if (StringComparer.Ordinal.Equals(runtimeRole, migrationRole) ||
            !IsRoleName(runtimeRole) || !IsRoleName(migrationRole))
            throw new InvalidOperationException("Identity broker bootstrap requires two distinct PostgreSQL role names.");
        if (!IsDatabaseName(databaseName))
            throw new InvalidOperationException("Identity broker bootstrap database name is invalid.");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.Database != "postgres" ||
            (!string.IsNullOrEmpty(builder.Username) && builder.Username != adminUsername) ||
            !string.IsNullOrEmpty(builder.Password))
            throw new InvalidOperationException(
                "Identity broker bootstrap requires the password-free approved Entra admin connection to postgres.");
        builder.Username = adminUsername;
        if (builder.SslMode != SslMode.VerifyFull ||
            !StringComparer.Ordinal.Equals(builder.Host, "127.0.0.1") ||
            builder.Port != 15432)
            throw new InvalidOperationException(
                "Identity broker bootstrap requires VerifyFull TLS through the installer loopback tunnel on 127.0.0.1:15432.");

        return new IdentityBrokerPostgresBootstrapOptions(
            builder.ConnectionString,
            postgresHost,
            adminUsername,
            databaseName,
            runtimeRole,
            runtimePrincipal,
            migrationRole,
            migrationPrincipal);
    }

    public static async Task RunAsync(
        IConfiguration configuration,
        Func<TokenCredential>? credentialFactory = null,
        CancellationToken cancellationToken = default,
        bool verifyOnly = false)
    {
        var options = ReadOptions(configuration);
        var credential = credentialFactory?.Invoke() ?? new AzureCliCredential();
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]),
            cancellationToken).ConfigureAwait(false);
        var connectionString = new NpgsqlConnectionStringBuilder(options.ConnectionString)
        {
            Password = token.Token,
            ApplicationName = "agentweaver-identity-postgres-bootstrap",
        }.ConnectionString;

        await using var adminDataSource = CreateDataSource(connectionString, options.PostgresHost);
        await using var adminConnection = await adminDataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await AssertAdminConnectionAsync(adminConnection, options.AdminUsername, cancellationToken)
            .ConfigureAwait(false);

        if (verifyOnly)
        {
            await VerifyStateAsync(adminConnection, connectionString, options, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine(
                $"IDENTITY_POSTGRES_BOOTSTRAP_VERIFIED database={options.DatabaseName} schema=identity_broker");
            return;
        }

        var roles = await ReadRoleNamesAsync(adminConnection, options, cancellationToken).ConfigureAwait(false);
        var databaseExists = await DatabaseExistsAsync(adminConnection, options.DatabaseName, cancellationToken)
            .ConfigureAwait(false);
        var schemaExists = databaseExists &&
            await SchemaExistsAsync(connectionString, options, cancellationToken).ConfigureAwait(false);
        if (roles.Count == 0 && !databaseExists)
        {
            await CreateInitialStateAsync(adminConnection, connectionString, options, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (roles.Count != 2 || !databaseExists || !schemaExists)
        {
            throw new InvalidOperationException(
                "PostgreSQL contains a partial or unowned Identity bootstrap. Refusing to adopt or reset it.");
        }

        await VerifyStateAsync(adminConnection, connectionString, options, cancellationToken).ConfigureAwait(false);
        var databaseConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = options.DatabaseName,
        }.ConnectionString;
        await using var databaseSource = CreateDataSource(databaseConnectionString, options.PostgresHost);
        await using var databaseConnection = await databaseSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var grantsApplied = await ApplyRuntimeGrantsIfMigratedAsync(databaseConnection,
            options.RuntimeRole, options.MigrationRole, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(grantsApplied
            ? $"IDENTITY_POSTGRES_RUNTIME_GRANTS_APPLIED runtimeRole={options.RuntimeRole} explicitDmlTables=9 historySelect=TRUE historyWrite=FALSE schemaCreate=FALSE"
            : "IDENTITY_POSTGRES_RUNTIME_GRANTS_PENDING migrationHistory=absent");
        Console.WriteLine($"IDENTITY_POSTGRES_BOOTSTRAP_OK database={options.DatabaseName} schema=identity_broker");
    }

    internal static async Task<bool> ApplyRuntimeGrantsIfMigratedAsync(
        NpgsqlConnection connection,
        string runtimeRole,
        string migrationRole,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var history = new NpgsqlCommand(
            "SELECT to_regclass('identity_broker.__ef_migrations_history') IS NOT NULL", connection, transaction))
        {
            if (await history.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                return false;
        }

        await using var resource = typeof(IdentityBrokerPostgresBootstrapCommand).Assembly.GetManifestResourceStream(
            "Agentweaver.Identity.Broker.postgres-identity-runtime-grants.sql")
            ?? throw new InvalidOperationException("The canonical Identity runtime grants are missing.");
        using var text = new StreamReader(resource);
        var definition = await text.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var tables = Regex.Matches(definition, @"'identity_broker', '([A-Za-z_]+)'")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        var statements = Regex.Matches(definition, @"SELECT format\([\s\S]*?\) \\gexec")
            .Select(match => match.Value.Replace(" \\gexec", "", StringComparison.Ordinal)).ToArray();
        if (tables.Length != 10 || !tables.Contains("__ef_migrations_history", StringComparer.Ordinal) ||
            statements.Length != 8)
            throw new InvalidOperationException("The canonical Identity runtime grant contract is malformed.");

        await using (var ownership = new NpgsqlCommand("""
            SELECT count(*) = 10
            FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace schema ON schema.oid = relation.relnamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid = relation.relowner
            WHERE schema.nspname = 'identity_broker' AND relation.relname = ANY(@tables)
              AND relation.relkind = 'r' AND owner.rolname = @migrationRole
            """, connection, transaction))
        {
            ownership.Parameters.AddWithValue("tables", tables);
            ownership.Parameters.AddWithValue("migrationRole", migrationRole);
            if (await ownership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("The exact post-migration tables are missing or have a different owner.");
        }

        foreach (var sql in statements)
        {
            await using var format = new NpgsqlCommand(
                sql.Replace(":'runtime_role'", "@runtimeRole", StringComparison.Ordinal), connection, transaction);
            format.Parameters.AddWithValue("runtimeRole", runtimeRole);
            var grant = await format.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                ?? throw new InvalidOperationException("A canonical runtime grant did not resolve.");
            await ExecuteAsync(connection, transaction, grant, cancellationToken).ConfigureAwait(false);
        }
        foreach (var table in tables)
        {
            var history = table == "__ef_migrations_history";
            await using var readback = new NpgsqlCommand("""
                SELECT has_table_privilege(@runtimeRole, @relation, 'SELECT')
                  AND has_table_privilege(@runtimeRole, @relation, 'INSERT') = @dml
                  AND has_table_privilege(@runtimeRole, @relation, 'UPDATE') = @dml
                  AND has_table_privilege(@runtimeRole, @relation, 'DELETE') = @dml
                  AND NOT has_table_privilege(@runtimeRole, @relation, 'TRUNCATE,REFERENCES,TRIGGER')
                  AND NOT has_schema_privilege(@runtimeRole, 'identity_broker', 'CREATE')
                """, connection, transaction);
            readback.Parameters.AddWithValue("runtimeRole", runtimeRole);
            readback.Parameters.AddWithValue("relation", $"identity_broker.{QuoteIdentifier(table)}");
            readback.Parameters.AddWithValue("dml", !history);
            if (await readback.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("The exact Identity runtime table privileges did not read back.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static NpgsqlDataSource CreateDataSource(string connectionString, string postgresHost)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseSslClientAuthenticationOptionsCallback(options => options.TargetHost = postgresHost);
        return builder.Build();
    }

    private static async Task AssertAdminConnectionAsync(
        NpgsqlConnection connection,
        string expectedAdmin,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT current_user, current_database()", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(0), expectedAdmin) ||
            !StringComparer.Ordinal.Equals(reader.GetString(1), "postgres"))
            throw new InvalidOperationException("PostgreSQL bootstrap did not connect as the approved Entra admin to postgres.");
    }

    private static async Task<HashSet<string>> ReadRoleNamesAsync(
        NpgsqlConnection connection,
        IdentityBrokerPostgresBootstrapOptions options,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT rolname FROM pg_catalog.pg_roles WHERE rolname = ANY(@roles)", connection);
        command.Parameters.AddWithValue("roles", new[] { options.RuntimeRole, options.MigrationRole });
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<bool> DatabaseExistsAsync(
        NpgsqlConnection connection,
        string database,
        CancellationToken cancellationToken)
    {
        var databases = new List<string>();
        await using (var list = new NpgsqlCommand(
            "SELECT datname FROM pg_catalog.pg_database WHERE NOT datistemplate ORDER BY datname", connection))
        {
            await using var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                databases.Add(reader.GetString(0));
        }

        if (databases.Any(name => !SystemDatabases.Contains(name) && name != database))
            throw new InvalidOperationException("PostgreSQL contains an unexpected user database. Refusing initial bootstrap.");
        return databases.Contains(database, StringComparer.Ordinal);
    }

    private static async Task<bool> SchemaExistsAsync(
        string adminConnectionString,
        IdentityBrokerPostgresBootstrapOptions options,
        CancellationToken cancellationToken)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = options.DatabaseName,
        }.ConnectionString;
        await using var dataSource = CreateDataSource(connectionString, options.PostgresHost);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.to_regnamespace('identity_broker') IS NOT NULL", connection);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task CreateInitialStateAsync(
        NpgsqlConnection adminConnection,
        string adminConnectionString,
        IdentityBrokerPostgresBootstrapOptions options,
        CancellationToken cancellationToken)
    {
        await using (var createDatabase = new NpgsqlCommand(
            $"CREATE DATABASE {QuoteIdentifier(options.DatabaseName)}", adminConnection))
            await createDatabase.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using (var transaction = await adminConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(adminConnection, transaction,
                $"SELECT pg_catalog.pgaadauth_create_principal_with_oid({QuoteLiteral(options.RuntimeRole)}, {QuoteLiteral(options.RuntimePrincipalObjectId)}, 'service', false, false)",
                cancellationToken);
            await ExecuteAsync(adminConnection, transaction,
                $"SELECT pg_catalog.pgaadauth_create_principal_with_oid({QuoteLiteral(options.MigrationRole)}, {QuoteLiteral(options.MigrationPrincipalObjectId)}, 'service', false, false)",
                cancellationToken);
            await ExecuteAsync(adminConnection, transaction,
                $"ALTER ROLE {QuoteIdentifier(options.RuntimeRole)} NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT",
                cancellationToken);
            await ExecuteAsync(adminConnection, transaction,
                $"ALTER ROLE {QuoteIdentifier(options.MigrationRole)} NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var databaseConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = options.DatabaseName,
        }.ConnectionString;
        await using var dataSource = CreateDataSource(databaseConnectionString, options.PostgresHost);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var schemaTransaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var database = QuoteIdentifier(options.DatabaseName);
        var runtime = QuoteIdentifier(options.RuntimeRole);
        var migration = QuoteIdentifier(options.MigrationRole);
        foreach (var sql in new[]
                 {
                     "REVOKE CREATE ON SCHEMA public FROM PUBLIC",
                     $"REVOKE CONNECT, TEMPORARY ON DATABASE {database} FROM PUBLIC",
                     $"REVOKE ALL ON DATABASE {database} FROM {runtime}",
                     $"REVOKE ALL ON DATABASE {database} FROM {migration}",
                     $"GRANT CONNECT ON DATABASE {database} TO {runtime}",
                     $"GRANT CONNECT ON DATABASE {database} TO {migration}",
                     $"CREATE SCHEMA identity_broker AUTHORIZATION {migration}",
                     "REVOKE ALL ON SCHEMA identity_broker FROM PUBLIC",
                     $"REVOKE ALL ON SCHEMA identity_broker FROM {runtime}",
                     $"GRANT USAGE ON SCHEMA identity_broker TO {runtime}",
                 })
            await ExecuteAsync(connection, schemaTransaction, sql, cancellationToken).ConfigureAwait(false);
        await schemaTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task VerifyStateAsync(
        NpgsqlConnection adminConnection,
        string adminConnectionString,
        IdentityBrokerPostgresBootstrapOptions options,
        CancellationToken cancellationToken)
    {
        var roles = new[] { options.RuntimeRole, options.MigrationRole };
        await using (var roleCheck = new NpgsqlCommand(
            """
            SELECT count(*) = 2
            FROM pg_catalog.pg_roles
            WHERE rolname = ANY(@roles)
              AND rolcanlogin
              AND NOT rolsuper
              AND NOT rolcreatedb
              AND NOT rolcreaterole
              AND NOT rolreplication
              AND NOT rolinherit
            """, adminConnection))
        {
            roleCheck.Parameters.AddWithValue("roles", roles);
            if (!Convert.ToBoolean(await roleCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
                throw new InvalidOperationException("PostgreSQL workload roles differ from the approved role attributes.");
        }

        await using (var principalCheck = new NpgsqlCommand(
            PrincipalCheckSql, adminConnection))
        {
            principalCheck.Parameters.AddWithValue("runtimeRole", options.RuntimeRole);
            principalCheck.Parameters.AddWithValue("runtimePrincipal", options.RuntimePrincipalObjectId);
            principalCheck.Parameters.AddWithValue("migrationRole", options.MigrationRole);
            principalCheck.Parameters.AddWithValue("migrationPrincipal", options.MigrationPrincipalObjectId);
            if (!Convert.ToBoolean(await principalCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
                throw new InvalidOperationException("PostgreSQL workload roles do not map to the two approved UAMI principals.");
        }

        await using (var memberships = new NpgsqlCommand(OperatorMembershipCheckSql, adminConnection))
        {
            memberships.Parameters.AddWithValue("adminUsername", options.AdminUsername);
            memberships.Parameters.AddWithValue("roles", roles);
            if (!Convert.ToBoolean(await memberships.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
                throw new InvalidOperationException(
                    "PostgreSQL native Entra administrator memberships differ from the approved admin-only options.");
        }

        await using (var setRole = new NpgsqlCommand(SetRoleCheckSql, adminConnection))
        {
            setRole.Parameters.AddWithValue("runtimeRole", options.RuntimeRole);
            setRole.Parameters.AddWithValue("migrationRole", options.MigrationRole);
            setRole.Parameters.AddWithValue("adminUsername", options.AdminUsername);
            if (!Convert.ToBoolean(await setRole.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
                throw new InvalidOperationException(
                    "A PostgreSQL workload role can SET ROLE to another workload or administrator role.");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = options.DatabaseName,
        }.ConnectionString;
        await using var dataSource = CreateDataSource(connectionString, options.PostgresHost);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var grantsSql = $"""
            SELECT
              has_database_privilege({QuoteLiteral(options.RuntimeRole)}, {QuoteLiteral(options.DatabaseName)}, 'CONNECT')
              AND has_database_privilege({QuoteLiteral(options.MigrationRole)}, {QuoteLiteral(options.DatabaseName)}, 'CONNECT')
              AND NOT has_database_privilege({QuoteLiteral(options.RuntimeRole)}, {QuoteLiteral(options.DatabaseName)}, 'CREATE')
              AND NOT has_database_privilege({QuoteLiteral(options.MigrationRole)}, {QuoteLiteral(options.DatabaseName)}, 'CREATE')
              AND NOT has_database_privilege({QuoteLiteral(options.RuntimeRole)}, {QuoteLiteral(options.DatabaseName)}, 'TEMPORARY')
              AND NOT has_database_privilege({QuoteLiteral(options.MigrationRole)}, {QuoteLiteral(options.DatabaseName)}, 'TEMPORARY')
              AND (SELECT nspowner = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = {QuoteLiteral(options.MigrationRole)})
                   FROM pg_catalog.pg_namespace WHERE nspname = 'identity_broker')
              AND has_schema_privilege({QuoteLiteral(options.RuntimeRole)}, 'identity_broker', 'USAGE')
              AND NOT has_schema_privilege({QuoteLiteral(options.RuntimeRole)}, 'identity_broker', 'CREATE')
              AND has_schema_privilege({QuoteLiteral(options.MigrationRole)}, 'identity_broker', 'CREATE')
            """;
        await using var grantsCheck = new NpgsqlCommand(grantsSql, connection);
        if (!Convert.ToBoolean(await grantsCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
            throw new InvalidOperationException("PostgreSQL database and schema grants differ from the approved bootstrap.");

        Console.WriteLine(
            $"IDENTITY_POSTGRES_PRIVILEGES database={options.DatabaseName} schemaOwner={options.MigrationRole} " +
            $"runtimeRole={options.RuntimeRole} migrationRole={options.MigrationRole} " +
            $"principalType=service isAdmin=0 " +
            $"adminMemberships={options.AdminUsername}->{options.RuntimeRole}[ADMIN=TRUE,INHERIT=FALSE,SET=FALSE]," +
            $"{options.AdminUsername}->{options.MigrationRole}[ADMIN=TRUE,INHERIT=FALSE,SET=FALSE] " +
            "runtimeSetMigration=FALSE migrationSetRuntime=FALSE runtimeSetAdmin=FALSE migrationSetAdmin=FALSE " +
            "runtimeSetAzurePgAdmin=FALSE migrationSetAzurePgAdmin=FALSE");
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";

    private static bool IsRoleName(string value) =>
        value.Length is > 0 and <= 63 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static bool IsDatabaseName(string value) =>
        value.Length is > 0 and <= 63 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static string Required(string? value, string key) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required configuration '{key}'.");
}
