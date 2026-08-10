using System.Text;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Memory;

public sealed class RunAuthorshipCapabilityStoreTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _services;

    public RunAuthorshipCapabilityStoreTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(options => options.UseSqlite(_connection));
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Capability_IsHashedAndValidAcrossStoreInstances()
    {
        const string runId = "run-replica-safe";
        const string token = "sensitive-bearer-token";
        var firstReplica = new EfRunAuthorshipCapabilityStore(_services.GetRequiredService<IServiceScopeFactory>());
        var secondReplica = new EfRunAuthorshipCapabilityStore(_services.GetRequiredService<IServiceScopeFactory>());

        await firstReplica.RegisterAsync(runId, token, DateTimeOffset.UtcNow.AddMinutes(5), default);

        (await secondReplica.ValidateAsync(runId, token, default)).Should().BeTrue();
        (await secondReplica.ValidateAsync(runId, "wrong-token", default)).Should().BeFalse();

        await using var scope = _services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<MemoryDbContext>()
            .RunAuthorshipCapabilities.SingleAsync();
        persisted.TokenHash.Should().NotEqual(Encoding.UTF8.GetBytes(token));
    }

    [Fact]
    public async Task ExpiredCapability_IsRejected()
    {
        var store = new EfRunAuthorshipCapabilityStore(_services.GetRequiredService<IServiceScopeFactory>());
        await store.RegisterAsync("expired-run", "token", DateTimeOffset.UtcNow.AddSeconds(-1), default);

        (await store.ValidateAsync("expired-run", "token", default)).Should().BeFalse();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
