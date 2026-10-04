using Agentweaver.Abstractions;
using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class IdentityBrokerMigrationCommandTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task MigrationCommandRequiresItsOwnExplicitConfiguration()
    {
        var configuration = new ConfigurationBuilder().Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IdentityBrokerMigrationCommand.RunAsync(configuration));

        Assert.Contains("ConnectionStrings:IdentityBrokerMigration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigrationIdentityOwnsSchemaAndRuntimeHasOnlyExplicitDml()
    {
        var adminConnectionString = await postgres.CreateDatabaseAsync();
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString);
        var database = adminBuilder.Database
            ?? throw new InvalidOperationException("Testcontainers did not provide a database name.");
        var runtimeRole = "identity_runtime_" + Guid.NewGuid().ToString("N");
        var migrationRole = "identity_migration_" + Guid.NewGuid().ToString("N");
        var runtimePassword = Guid.NewGuid().ToString("N");
        var migrationPassword = Guid.NewGuid().ToString("N");
        await CreateScopedRolesAsync(
            adminConnectionString, database, runtimeRole, runtimePassword,
            migrationRole, migrationPassword);

        var runtimeConnectionString = CreateTokenConnectionString(adminBuilder, runtimeRole);
        var migrationConnectionString = CreateTokenConnectionString(adminBuilder, migrationRole);
        var tenantId = Guid.NewGuid().ToString();
        var clientId = Guid.NewGuid().ToString();
        var tokenFilePath = Path.GetFullPath(Path.Combine("artifacts", "generated-migration-token.jwt"));
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:IdentityBrokerMigration"] = migrationConnectionString,
            ["IdentityBroker:Migration:WorkloadIdentityTenantId"] = tenantId,
            ["IdentityBroker:Migration:WorkloadIdentityClientId"] = clientId,
            ["IdentityBroker:Migration:WorkloadIdentityTokenFilePath"] = tokenFilePath,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var migrationCredential = new PasswordTokenCredential(migrationPassword);
        IdentityBrokerMigrationIdentity? usedIdentity = null;

        await IdentityBrokerMigrationCommand.RunAsync(
            configuration,
            identity =>
            {
                usedIdentity = identity;
                return migrationCredential;
            },
            SslMode.Disable);

        Assert.Equal(new IdentityBrokerMigrationIdentity(tenantId, clientId, tokenFilePath), usedIdentity);
        Assert.Contains("https://ossrdbms-aad.database.windows.net/.default", migrationCredential.Scopes);
        await GrantRuntimePermissionsAsync(adminConnectionString, runtimeRole);

        var runtimeCredential = new PasswordTokenCredential(runtimePassword);
        await using var runtimeDataSource = IdentityBrokerPostgresDataSource.Create(
            runtimeConnectionString, runtimeCredential, SslMode.Disable);
        var options = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(runtimeDataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema))
            .Options;
        await IdentityBrokerMigrator.VerifyMigrationsAppliedAsync(runtimeDataSource, options);

        await using (var db = new IdentityBrokerDbContext(options))
        {
            var user = await new BrokerUserProvisioner(db, TimeProvider.System).ProvisionAsync(
                "https://issuer.generated.invalid", "generated-subject", null, null, CancellationToken.None);
            Assert.NotEqual(Guid.Empty, user.Id);
        }

        var runtimePasswordConnection = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Username = runtimeRole,
            Password = runtimePassword,
        };
        await using var idp = await FakeIdentityProvider.StartAsync();
        await using var factory = new IdentityBrokerWebApplicationFactory(
            runtimePasswordConnection.ConnectionString, idp);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://broker.test.local"),
        });
        Assert.Equal(200, (int)(await client.GetAsync("/health/ready")).StatusCode);

        await using var runtimeConnection = await runtimeDataSource.OpenConnectionAsync();
        await using (var createSchema = new NpgsqlCommand("CREATE SCHEMA identity_broker_runtime_denied", runtimeConnection))
            await Assert.ThrowsAsync<PostgresException>(() => createSchema.ExecuteNonQueryAsync());
        await using (var mutateHistory = new NpgsqlCommand(
            "INSERT INTO identity_broker.__ef_migrations_history (\"MigrationId\", \"ProductVersion\") VALUES ('generated', 'test')",
            runtimeConnection))
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => mutateHistory.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        await using var ownerCheck = new NpgsqlCommand(
            """
            SELECT pg_catalog.pg_get_userbyid(nspowner)
            FROM pg_catalog.pg_namespace
            WHERE nspname = 'identity_broker'
            """,
            runtimeConnection);
        Assert.Equal(migrationRole, await ownerCheck.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RuntimeStartupFailsWhenSchemaHasNotBeenMigrated()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema))
            .Options;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IdentityBrokerMigrator.VerifyMigrationsAppliedAsync(dataSource, options));

        Assert.Contains("Run the approved Identity broker migration Job", exception.Message, StringComparison.Ordinal);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var check = new NpgsqlCommand("SELECT pg_catalog.to_regnamespace('identity_broker') IS NULL", connection);
        Assert.True((bool)(await check.ExecuteScalarAsync())!);
    }

    private static async Task CreateScopedRolesAsync(
        string adminConnectionString,
        string database,
        string runtimeRole,
        string runtimePassword,
        string migrationRole,
        string migrationPassword)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        var sql = $"""
            CREATE ROLE {QuoteIdentifier(runtimeRole)} LOGIN PASSWORD {QuoteLiteral(runtimePassword)}
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;
            CREATE ROLE {QuoteIdentifier(migrationRole)} LOGIN PASSWORD {QuoteLiteral(migrationPassword)}
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;
            REVOKE CONNECT, TEMPORARY ON DATABASE {QuoteIdentifier(database)} FROM PUBLIC;
            GRANT CONNECT ON DATABASE {QuoteIdentifier(database)} TO {QuoteIdentifier(runtimeRole)};
            GRANT CONNECT ON DATABASE {QuoteIdentifier(database)} TO {QuoteIdentifier(migrationRole)};
            CREATE SCHEMA identity_broker AUTHORIZATION {QuoteIdentifier(migrationRole)};
            REVOKE ALL ON SCHEMA identity_broker FROM PUBLIC;
            GRANT USAGE ON SCHEMA identity_broker TO {QuoteIdentifier(runtimeRole)};
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task GrantRuntimePermissionsAsync(string adminConnectionString, string runtimeRole)
    {
        string[] tables =
        [
            "broker_users",
            "pending_authorizations",
            "secret_grant_heads",
            "secret_grant_revisions",
            "secret_grant_operations",
            "OpenIddictApplications",
            "OpenIddictAuthorizations",
            "OpenIddictScopes",
            "OpenIddictTokens",
        ];
        var qualifiedTables = string.Join(", ", tables.Select(table =>
            $"identity_broker.{QuoteIdentifier(table)}"));
        var sql = $"""
            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE {qualifiedTables} TO {QuoteIdentifier(runtimeRole)};
            GRANT SELECT ON TABLE identity_broker.__ef_migrations_history TO {QuoteIdentifier(runtimeRole)};
            """;

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateTokenConnectionString(NpgsqlConnectionStringBuilder admin, string role)
    {
        var connection = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Username = role,
            Pooling = false,
            SslMode = SslMode.Disable,
        };
        connection.Remove("Password");
        return connection.ConnectionString;
    }

    private static string QuoteIdentifier(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private sealed class PasswordTokenCredential(string token) : TokenCredential
    {
        public List<string> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synchronous token acquisition is disabled.");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scopes.AddRange(requestContext.Scopes);
            return ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }
}
