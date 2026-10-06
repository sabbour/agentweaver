using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

[CollectionDefinition("Sessions PostgreSQL")]
public sealed class SessionsPostgresCollection : ICollectionFixture<SessionsPostgresFixture>;

public sealed class SessionsPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string DatabaseName => new NpgsqlConnectionStringBuilder(_container.GetConnectionString()).Database!;

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
