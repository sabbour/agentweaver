using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.Persistence.Postgres.Tests;

[CollectionDefinition("PostgreSQL")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}
