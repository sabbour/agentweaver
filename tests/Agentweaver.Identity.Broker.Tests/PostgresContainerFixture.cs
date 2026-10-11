using System.Data;
using Agentweaver.Identity.Broker;
using Microsoft.EntityFrameworkCore;
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
    private const string ConnectionCountSql = """
        SELECT count(*) FROM pg_stat_activity
        WHERE datname = current_database() AND pid <> pg_backend_pid()
        """;

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

    public async Task<string> CreateMigratedDatabaseAsync()
    {
        var connectionString = await CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema))
            .Options;
        await IdentityBrokerMigrator.MigrateAsync(dataSource, options);
        return connectionString;
    }

    public string GetConnectionString() => _container.GetConnectionString();

    public static async Task<long> CountConnectionsAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var observer = new NpgsqlConnection(builder.ConnectionString);
        await observer.OpenAsync();
        await using var count = new NpgsqlCommand(ConnectionCountSql, observer);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    public static async Task<long> WaitForNoConnectionsAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        if (string.IsNullOrEmpty(builder.Database))
            throw new InvalidOperationException("The fixture connection must name its exact database.");
        await using var observer = new NpgsqlConnection(builder.ConnectionString);
        await using var count = new NpgsqlCommand(ConnectionCountSql, observer);
        return await WaitForNoConnectionsAsync(async cancellationToken =>
        {
            if (observer.State != ConnectionState.Open)
                await observer.OpenAsync(cancellationToken);
            return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }, builder.Database, TimeSpan.FromSeconds(5));
    }

    internal static async Task<long> WaitForNoConnectionsAsync(
        Func<CancellationToken, Task<long>> countConnections,
        string databaseName,
        TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        long? remainingConnections = null;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                remainingConnections = await countConnections(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if (remainingConnections == 0)
                    return 0;
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            }
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Fixture cleanup for database '{databaseName}' did not reach zero connections within {timeout}; " +
                $"last observed remaining connections={remainingConnections?.ToString() ?? "not observed"}.",
                error);
        }
    }
}
