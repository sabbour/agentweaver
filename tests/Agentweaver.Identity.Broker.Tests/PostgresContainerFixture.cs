using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[CollectionDefinition("IdentityBrokerPostgres")]
public sealed class IdentityBrokerPostgresCollection : ICollectionFixture<PostgresContainerFixture>;

/// <summary>
/// Starts a single shared Postgres container for the whole test run. Each test (or test
/// class) that needs isolation creates its own database inside this container via
/// <see cref="CreateDatabaseAsync"/>, which keeps container startup cost (several seconds)
/// out of the per-test critical path while still giving every test a fully isolated schema
/// namespace, suitable for the broker's hardcoded "identity_broker" schema name.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates a fresh, uniquely named database and returns a connection string to it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var databaseName = "test_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName,
        };
        return builder.ConnectionString;
    }

    public string GetConnectionString() => _container.GetConnectionString();
}
