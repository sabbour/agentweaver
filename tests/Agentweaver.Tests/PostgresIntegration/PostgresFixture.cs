using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace Agentweaver.Tests.PostgresIntegration;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("awtest").WithUsername("awtest").WithPassword("awtest")
        .WithCleanUp(true).Build();

    public IDbContextFactory<MemoryDbContext> Factory { get; private set; } = null!;
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<MemoryDbContext>(opts =>
            opts.UseNpgsql(ConnectionString,
                n => n.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres")));
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        Factory = sp.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
    public Task<MemoryDbContext> CreateDbContextAsync() => Factory.CreateDbContextAsync();
}

[CollectionDefinition("PostgresIntegration", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }